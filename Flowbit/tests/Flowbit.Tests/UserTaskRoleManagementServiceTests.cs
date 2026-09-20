using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.DependencyInjection;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.DependencyInjection;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class UserTaskRoleManagementServiceTests(PostgresApiFixture fixture)
{
    private const string RoleManager = "RoleManager";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ProductionRegistrationsShareRepositoriesAndUnitOfWorkWithinEachScope()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Flowbit"] =
                    "Host=127.0.0.1;Port=1;Database=role_management_composition;Username=test;Password=test"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowContextOptions());
        services.AddServiceLayer();
        services.AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var firstRoles = VerifyScope(first.ServiceProvider);
        var secondRoles = VerifyScope(second.ServiceProvider);
        Assert.NotSame(firstRoles, secondRoles);
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IWorkflowRuntimeRepository>(),
            second.ServiceProvider.GetRequiredService<IWorkflowRuntimeRepository>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            second.ServiceProvider.GetRequiredService<IUnitOfWork>());

        static IUserTaskRoleManagementService VerifyScope(IServiceProvider scope)
        {
            var repository = scope.GetRequiredService<WorkflowRuntimeRepository>();
            Assert.Same(repository, scope.GetRequiredService<IWorkflowRuntimeRepository>());
            var unitOfWork = scope.GetRequiredService<IUnitOfWork>();
            Assert.Same(unitOfWork, scope.GetRequiredService<IUnitOfWork>());
            var roles = scope.GetRequiredService<IUserTaskRoleManagementService>();
            Assert.IsType<UserTaskRoleManagementService>(roles);
            Assert.Same(roles, scope.GetRequiredService<IUserTaskRoleManagementService>());
            var engine = scope.GetRequiredService<WorkflowEngineService>();
            Assert.Same(engine, scope.GetRequiredService<IWorkflowEngineService>());
            Assert.NotSame(roles, engine);
            return roles;
        }
    }

    [Fact]
    public async Task RuntimeRepositoryAndUnitOfWorkShareOneUncommittedTransaction()
    {
        await using var provider = CreateIsolatedProvider(fixture.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var definitions = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var key = $"role-tx-share-{Guid.NewGuid():N}";
        await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        db.WorkflowDefinitions.Add(new WorkflowDefinitionEntity
        {
            Name = key, WorkflowKey = key, Version = 1, IsPublished = true,
            Definition = new WorkflowModel { Id = key, Name = key }
        });
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        var written = await db.WorkflowDefinitions.SingleAsync(row => row.WorkflowKey == key);
        Assert.NotNull(await definitions.GetAsync(written.Id, CancellationToken.None));

        await using var outside = fixture.CreateDbContext();
        Assert.Null(await outside.WorkflowDefinitions.SingleOrDefaultAsync(row => row.WorkflowKey == key));
    }

    [Theory]
    [InlineData("afterReplace")]
    [InlineData("onSave")]
    [InlineData("onCommit")]
    public async Task NormalRoleChangeRollsBackAfterInternalReplaceFlush(string fault)
    {
        var seed = await SeedNormalAsync();
        var snapshot = await SnapshotAsync(seed.InstanceId);
        var state = new FaultState();
        ApplyFault(state, fault);
        await using var provider = CreateIsolatedProvider(fixture.ConnectionString, state);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ChangeUserTaskRolesAsync(seed.TaskId, Replacement(seed.PolicyId), Manager(), CancellationToken.None));
        Assert.Contains("injected", error.Message, StringComparison.Ordinal);
        Assert.True(state.ReplaceRan);
        Assert.True(state.ReplacementId > 0);

        await AssertUnchangedAsync(snapshot);
        state.ThrowAfterReplace = state.ThrowOnSave = state.ThrowOnCommit = false;
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Entries());
        await AssertUnchangedAsync(snapshot);
    }

    [Theory]
    [InlineData("afterReplace")]
    [InlineData("onSave")]
    [InlineData("onCommit")]
    public async Task MultiInstanceRoleChangeRollsBackUnfinishedChildrenTogether(string fault)
    {
        var seed = await SeedMultiInstanceAsync();
        var snapshot = await SnapshotAsync(seed.InstanceId);
        var state = new FaultState();
        ApplyFault(state, fault);
        await using var provider = CreateIsolatedProvider(fixture.ConnectionString, state);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ChangeMultiInstanceRolesAsync(
                seed.ExecutionId, Replacement(seed.PolicyId), Manager(), CancellationToken.None));
        Assert.True(state.ReplaceRan);
        Assert.True(state.ReplacementId > 0);
        await AssertUnchangedAsync(snapshot);
        state.ThrowAfterReplace = state.ThrowOnSave = state.ThrowOnCommit = false;
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Entries());
        await AssertUnchangedAsync(snapshot);
    }

    [Fact]
    public async Task ConcurrentMultiInstanceChangesWaitForTheWinnerAndRecheckItsPolicy()
    {
        var seed = await SeedMultiInstanceAsync();
        var before = await SnapshotAsync(seed.InstanceId);
        var gate = new FaultState { HoldAfterReplace = true };
        await using var winnerProvider = CreateIsolatedProvider(fixture.ConnectionString, gate);
        await using var contenderProvider = CreateIsolatedProvider(fixture.ConnectionString);
        await using var winnerScope = winnerProvider.CreateAsyncScope();
        await using var contenderScope = contenderProvider.CreateAsyncScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contenderDb = contenderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await winnerDb.Database.OpenConnectionAsync(ct);
        await contenderDb.Database.OpenConnectionAsync(ct);
        var winnerPid = ((NpgsqlConnection)winnerDb.Database.GetDbConnection()).ProcessID;
        var contenderPid = ((NpgsqlConnection)contenderDb.Database.GetDbConnection()).ProcessID;
        Assert.NotEqual(winnerPid, contenderPid);
        var winner = winnerScope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>()
            .ChangeMultiInstanceRolesAsync(seed.ExecutionId, Replacement(seed.PolicyId), Manager(), ct);
        Task<UserTaskRolesChangeAckDto?>? contender = null;
        try
        {
            // Hold the real transaction after its internal flush, before audit/save/commit.
            await gate.ReplacementFlushed.Task.WaitAsync(ct);
            contender = contenderScope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>()
                .ChangeMultiInstanceRolesAsync(seed.ExecutionId,
                    new ChangeUserTaskRolesRequest(seed.PolicyId, ["Finance"], [new(201, [])], null), Manager(), ct);

            await using var observer = new NpgsqlConnection(fixture.ConnectionString);
            await observer.OpenAsync(ct);
            await using var blocked = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE pid = @contender AND wait_event_type = 'Lock'
                      AND @winner = ANY(pg_blocking_pids(pid))
                      AND query ILIKE '%workflow_instances%' AND query ILIKE '%FOR UPDATE%')
                """, observer);
            blocked.Parameters.AddWithValue("contender", contenderPid);
            blocked.Parameters.AddWithValue("winner", winnerPid);
            while (!(bool)(await blocked.ExecuteScalarAsync(ct))!)
            {
                Assert.False(contender.IsCompleted, "The competing edit must wait on the winner's instance lock.");
                await Task.Delay(25, ct);
            }
            Assert.False(winner.IsCompleted);
            Assert.False(contender.IsCompleted);
            await AssertUnchangedAsync(before);

            gate.ReleaseReplacement.TrySetResult();
            var changed = await winner;
            Assert.True(changed!.Changed);
            var conflict = await Assert.ThrowsAsync<WorkflowConflictException>(() => contender);
            Assert.Equal("The task roles changed; refresh and review them before trying again.", conflict.Message);

            await using var verify = fixture.CreateDbContext();
            Assert.Equal(before.Policies.Count + 1,
                await verify.UserTaskRolePolicies.CountAsync(row => row.InstanceId == seed.InstanceId, ct));
            Assert.Equal(1, await verify.InstanceHistory.CountAsync(
                row => row.InstanceId == seed.InstanceId && row.Note == "taskRolesChanged", ct));
            Assert.Equal(changed.Policy.RolePolicyId, await verify.MultiInstanceExecutions
                .Where(row => row.Id == seed.ExecutionId).Select(row => row.RolePolicyId).SingleAsync(ct));
            var children = await verify.UserTasks.Where(row => row.MultiInstanceExecutionId == seed.ExecutionId)
                .OrderBy(row => row.ItemIndex).ToListAsync(ct);
            Assert.Equal(seed.PolicyId, children[0].RolePolicyId);
            Assert.Equal(["Worker"], children[0].Roles);
            Assert.All(children.Skip(1), child =>
            {
                Assert.Equal(changed.Policy.RolePolicyId, child.RolePolicyId);
                Assert.Equal(["Legal"], child.Roles);
            });
        }
        finally
        {
            gate.ReleaseReplacement.TrySetResult();
            await timeout.CancelAsync();
            try
            {
                await Task.WhenAll(winner, contender ?? Task.FromResult<UserTaskRolesChangeAckDto?>(null))
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception) when (winner.IsCompleted && (contender?.IsCompleted ?? true))
            {
                // Observe failures before disposing scopes; the assertions above report them.
            }
        }
    }

    [Fact]
    public async Task SuccessfulNormalAndMultiInstanceChangesCommitPolicyAuditAndCounts()
    {
        await using var provider = CreateIsolatedProvider(fixture.ConnectionString);
        var claims = new Dictionary<string, string[]> { ["department"] = ["Legal", "Audit"] };
        var emptyClaims = new Dictionary<string, string[]>();

        var normal = await SeedNormalAsync();
        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>();
            var before = await service.GetUserTaskRolesAsync(normal.TaskId, Manager(), CancellationToken.None);
            var changed = await service.ChangeUserTaskRolesAsync(
                normal.TaskId, Replacement(normal.PolicyId, "  Coverage changed  "),
                AuditActor(claims), CancellationToken.None);
            Assert.True(changed!.Changed);
            Assert.NotEqual(normal.PolicyId, changed.Policy.RolePolicyId);
            Assert.Equal(["Legal"], changed.Policy.Roles);
            Assert.Equal(1, changed.Policy.ActiveTaskCount);
            Assert.Equal(0, changed.Policy.PendingTaskCount);
            await AssertHistoryAsync(before!, changed.Policy, claims, "Coverage changed");
        }

        var multi = await SeedMultiInstanceAsync();
        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>();
            var before = await service.GetMultiInstanceRolesAsync(multi.ExecutionId, Manager(), CancellationToken.None);
            var changed = await service.ChangeMultiInstanceRolesAsync(
                multi.ExecutionId, Replacement(multi.PolicyId, "   "),
                AuditActor(emptyClaims), CancellationToken.None);
            Assert.True(changed!.Changed);
            Assert.Equal(1, changed.Policy.ActiveTaskCount);
            Assert.Equal(1, changed.Policy.PendingTaskCount);
            await AssertHistoryAsync(before!, changed.Policy, emptyClaims, null);
            await using var db = fixture.CreateDbContext();
            var children = await db.UserTasks.AsNoTracking()
                .Where(task => task.MultiInstanceExecutionId == multi.ExecutionId)
                .OrderBy(task => task.ItemIndex)
                .ToListAsync();
            Assert.Equal(multi.PolicyId, children[0].RolePolicyId);
            Assert.Equal(["Worker"], children[0].Roles);
            Assert.All(children.Skip(1), child =>
            {
                Assert.Equal(changed.Policy.RolePolicyId, child.RolePolicyId);
                Assert.Equal(["Legal"], child.Roles);
            });
        }

        static ActorContext AuditActor(IReadOnlyDictionary<string, string[]> claims) =>
            new(" manager ", [" zeta ", RoleManager, "Audit", "ROLEMANAGER", " "], new Dictionary<string, string>())
            {
                ActingFor = "represented-user", DelegationId = 456, AuditClaims = claims
            };
    }

    [Fact]
    public async Task NoOpValidationAuthorizationAndMissingScopeCommitNoChanges()
    {
        var seed = await SeedNormalAsync();
        var snapshot = await SnapshotAsync(seed.InstanceId);
        await using var provider = CreateIsolatedProvider(fixture.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>();

        Assert.Null(await service.GetUserTaskRolesAsync(long.MaxValue, Manager(), CancellationToken.None));
        Assert.Null(await service.GetMultiInstanceRolesAsync(long.MaxValue, Manager(), CancellationToken.None));
        Assert.Null(await service.ChangeUserTaskRolesAsync(
            long.MaxValue, Replacement(1), Manager(), CancellationToken.None));

        var current = await service.GetUserTaskRolesAsync(seed.TaskId, Manager(), CancellationToken.None);
        Assert.NotNull(current);
        var noop = await service.ChangeUserTaskRolesAsync(
            seed.TaskId,
            new ChangeUserTaskRolesRequest(current.RolePolicyId + 99, ["Worker"], [new(201, ["Reviewer"])], null),
            Manager(), CancellationToken.None);
        Assert.False(noop!.Changed);
        Assert.Equal(current.RolePolicyId, noop.Policy.RolePolicyId);

        var forbidden = await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            service.GetUserTaskRolesAsync(seed.TaskId,
                new ActorContext("other", ["AssignmentManager"], new Dictionary<string, string>()),
                CancellationToken.None));
        Assert.Equal("You are not allowed to manage task roles for this workflow.", forbidden.Message);
        var invalid = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.ChangeUserTaskRolesAsync(seed.TaskId,
                new ChangeUserTaskRolesRequest(current.RolePolicyId, ["Legal"], [], null),
                Manager(), CancellationToken.None));
        Assert.Contains("exactly once", invalid.Message, StringComparison.Ordinal);
        await AssertUnchangedAsync(snapshot);
    }

    [Fact]
    public async Task ReadDoesNotWriteAndPreservesNullVersusEmptyAuditClaims()
    {
        var seed = await SeedNormalAsync();
        var snapshot = await SnapshotAsync(seed.InstanceId);
        await using var provider = CreateIsolatedProvider(fixture.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserTaskRoleManagementService>();
        var read = await service.GetUserTaskRolesAsync(seed.TaskId, Manager(), CancellationToken.None);
        Assert.Equal(seed.PolicyId, read!.RolePolicyId);
        await AssertUnchangedAsync(snapshot);

        var changed = await service.ChangeUserTaskRolesAsync(
            seed.TaskId, Replacement(seed.PolicyId, "  trimmed  "), Manager(null), CancellationToken.None);
        Assert.True(changed!.Changed);
        await using var db = fixture.CreateDbContext();
        var history = await db.InstanceHistory.SingleAsync(row =>
            row.InstanceId == seed.InstanceId && row.Note == "taskRolesChanged");
        Assert.Null(history.ActorClaimsJson);
        Assert.Equal("trimmed", history.Payload!.RootElement.GetProperty("reason").GetString());
        Assert.Null(history.ActionId);
        Assert.Equal("manager", history.PerformedBy);
    }

    private static ActorContext Manager(IReadOnlyDictionary<string, string[]>? auditClaims = null) =>
        new("manager", [RoleManager], new Dictionary<string, string>()) { AuditClaims = auditClaims };

    private static ChangeUserTaskRolesRequest Replacement(long policyId, string? reason = null) =>
        new(policyId, ["Legal"], [new(201, ["LegalAction"])], reason);

    private static void ApplyFault(FaultState state, string fault)
    {
        switch (fault)
        {
            case "afterReplace":
                state.ThrowAfterReplace = true;
                break;
            case "onSave":
                state.ThrowOnSave = true;
                break;
            default:
                state.ThrowOnCommit = true;
                break;
        }
    }

    private async Task<Seed> SeedNormalAsync()
    {
        var instance = await StartAsync(CreateModel());
        var task = Assert.Single((await ManagedAsync(instance.Id)).Items);
        var policy = await PolicyAsync($"/api/user-tasks/{task.UserTaskId}/roles");
        return new Seed(instance.Id, task.UserTaskId, 0, policy.RolePolicyId);
    }

    private async Task<Seed> SeedMultiInstanceAsync()
    {
        var model = CreateModel();
        model.Variables =
        [
            new VariableModel
            {
                Id = 1, Name = "results", DataType = "json",
                DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
            }
        ];
        var node = model.FlowNodes.Single(item => item.Id == 2);
        node.MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Sequential, Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "3", CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
            ResultVariable = "results"
        };
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition = "CountFlow(201) == 3";
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 202, SourceRef = 2, TargetRef = 3, Name = "Fallback", IsDefault = true, IsSelectable = false
        });
        var instance = await StartAsync(model);
        var tasks = (await ManagedAsync(instance.Id, "open")).Items.OrderBy(task => task.ItemIndex).ToList();
        var executionId = tasks[0].MultiInstanceExecutionId!.Value;
        using var first = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{tasks[0].UserTaskId}/flows/201",
            new TakeFlowRequest(null), "alice", ["Worker", "Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var policy = await PolicyAsync($"/api/multi-instance-executions/{executionId}/roles");
        return new Seed(instance.Id, 0, executionId, policy.RolePolicyId);
    }

    private async Task<StateSnapshot> SnapshotAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        var policies = await db.UserTaskRolePolicies.AsNoTracking()
            .Where(policy => policy.InstanceId == instanceId)
            .OrderBy(policy => policy.Id)
            .ToListAsync();
        var tasks = await db.UserTasks.AsNoTracking()
            .Where(task => task.InstanceId == instanceId)
            .OrderBy(task => task.Id)
            .ToListAsync();
        return new StateSnapshot(
            instanceId,
            policies.Select(policy => new PolicySnapshot(
                policy.Id, policy.Roles, policy.OutgoingFlowRolesJson.RootElement.GetRawText())).ToList(),
            tasks.Select(task => new TaskSnapshot(task.Id, task.RolePolicyId, task.Roles, task.Status,
                task.ClaimedBy, task.Assignee, task.UpdatedAt)).ToList(),
            await db.MultiInstanceExecutions.AsNoTracking()
                .Where(execution => execution.InstanceId == instanceId)
                .Select(execution => execution.RolePolicyId)
                .ToListAsync(),
            await db.InstanceHistory.AsNoTracking()
                .Where(history => history.InstanceId == instanceId)
                .Select(history => history.Id)
                .ToListAsync(),
            await db.WorkflowInstances.AsNoTracking()
                .Where(instance => instance.Id == instanceId)
                .Select(instance => instance.UpdatedAt)
                .SingleAsync());
    }

    private async Task AssertUnchangedAsync(StateSnapshot snapshot)
    {
        var current = await SnapshotAsync(snapshot.InstanceId);
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(current));
    }

    private async Task AssertHistoryAsync(
        UserTaskRolePolicyDto before,
        UserTaskRolePolicyDto after,
        IReadOnlyDictionary<string, string[]>? claims,
        string? reason)
    {
        await using var db = fixture.CreateDbContext();
        var history = Assert.Single(await db.InstanceHistory.AsNoTracking()
            .Where(row => row.InstanceId == before.InstanceId && row.Note == "taskRolesChanged")
            .ToListAsync());
        Assert.Equal("manager", history.PerformedBy);
        Assert.Null(history.ActingFor);
        Assert.Null(history.DelegationId);
        Assert.Equal(before.UserTaskId, history.UserTaskId);
        Assert.Equal(before.TokenId, history.TokenId);
        Assert.Equal(before.NodeId, history.FromStepId);
        Assert.Equal(before.NodeId, history.ToStepId);
        Assert.Equal(await db.WorkflowInstances.Where(row => row.Id == before.InstanceId)
            .Select(row => row.WorkflowDefinitionId).SingleAsync(), history.WorkflowDefinitionId);
        // MI edits have one parent-token audit; the execution ID belongs in its payload.
        Assert.Null(history.MultiInstanceExecutionId);
        Assert.Null(history.ItemIndex);
        Assert.Null(history.ActionId);
        var expectedPayload = JsonSerializer.SerializeToElement(new
        {
            previousRolePolicyId = before.RolePolicyId,
            newRolePolicyId = after.RolePolicyId,
            previousRoles = before.Roles,
            newRoles = after.Roles,
            previousFlowRoles = before.Flows.ToDictionary(flow => flow.FlowId, flow => flow.Roles),
            newFlowRoles = after.Flows.ToDictionary(flow => flow.FlowId, flow => flow.Roles),
            multiInstanceExecutionId = before.MultiInstanceExecutionId,
            affectedTaskCount = before.ActiveTaskCount + before.PendingTaskCount,
            performedByRoles = new[] { "Audit", RoleManager, "zeta" },
            reason
        });
        Assert.True(JsonElement.DeepEquals(expectedPayload, history.Payload!.RootElement),
            $"Expected audit {expectedPayload}; actual {history.Payload.RootElement}");
        if (claims is null)
            Assert.Null(history.ActorClaimsJson);
        else
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(claims),
                history.ActorClaimsJson!.RootElement));
    }

    private async Task<UserTaskRolePolicyDto> PolicyAsync(string url)
    {
        using var response = await SendAsync(HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<UserTaskRolePolicyDto>(JsonOptions)
            ?? throw new InvalidOperationException("Empty policy.");
    }

    private async Task<PagedResult<ManagedUserTaskDto>> ManagedAsync(long instanceId, string? status = null)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"/api/user-tasks/manage?instanceId={instanceId}&pageSize=200{(status is null ? "" : $"&status={status}")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<PagedResult<ManagedUserTaskDto>>(JsonOptions)
            ?? throw new InvalidOperationException("Empty page.");
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var workflowId = (await created.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
        using var started = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        return (await started.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body = null, string user = "manager", string[]? roles = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles ?? [RoleManager]);
        return await fixture.Client.SendAsync(request);
    }

    private static WorkflowModel CreateModel() => new()
    {
        Id = $"test-role-service-{Guid.NewGuid():N}", Name = "Role management service",
        InitialEventId = 1, TaskRoleManagementRoles = [RoleManager], TaskAssignmentRoles = ["AssignmentManager"],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, Roles = ["Worker"] },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete", Roles = ["Reviewer"] }
        ]
    };

    private static ServiceProvider CreateIsolatedProvider(string connectionString, FaultState? fault = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Flowbit"] = connectionString }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowContextOptions());
        services.AddServiceLayer();
        services.AddInfrastructure(configuration);
        if (fault is not null)
        {
            services.AddSingleton(fault);
            services.RemoveAll<IWorkflowRuntimeRepository>();
            services.AddScoped<IWorkflowRuntimeRepository>(provider =>
            {
                var proxy = DispatchProxy.Create<IWorkflowRuntimeRepository, FaultingRuntime>();
                ((FaultingRuntime)(object)proxy).Initialize(
                    provider.GetRequiredService<WorkflowRuntimeRepository>(),
                    provider.GetRequiredService<FaultState>());
                return proxy;
            });
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider =>
                new FaultingUnitOfWork(
                    new UnitOfWork(provider.GetRequiredService<AppDbContext>()),
                    provider.GetRequiredService<FaultState>()));
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record Seed(long InstanceId, long TaskId, long ExecutionId, long PolicyId);

    private sealed record PolicySnapshot(long Id, List<string> Roles, string FlowRoles);

    private sealed record TaskSnapshot(
        long Id, long? RolePolicyId, List<string> Roles, string Status,
        string? ClaimedBy, string? Assignee, DateTimeOffset UpdatedAt);

    private sealed record StateSnapshot(
        long InstanceId,
        List<PolicySnapshot> Policies,
        List<TaskSnapshot> Tasks,
        List<long?> ExecutionPolicies,
        List<long> HistoryIds,
        DateTimeOffset InstanceUpdatedAt);

    public sealed class FaultState
    {
        public bool HoldAfterReplace { get; init; }
        public TaskCompletionSource ReplacementFlushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReplacement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowAfterReplace { get; set; }
        public bool ThrowOnSave { get; set; }
        public bool ThrowOnCommit { get; set; }
        public bool ReplaceRan { get; set; }
        public long ReplacementId { get; set; }
    }

    public class FaultingRuntime : DispatchProxy
    {
        private IWorkflowRuntimeRepository inner = null!;
        private FaultState state = null!;

        public void Initialize(IWorkflowRuntimeRepository runtime, FaultState fault)
        {
            inner = runtime;
            state = fault;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(inner, args);
            if (targetMethod.Name == nameof(IWorkflowRuntimeRepository.ReplaceUserTaskRolePolicyAsync)
                && result is Task<UserTaskRolePolicyRecord> replace)
            {
                return AwaitReplace(replace, (CancellationToken)args![^1]!);
            }
            return result;
        }

        private async Task<UserTaskRolePolicyRecord> AwaitReplace(
            Task<UserTaskRolePolicyRecord> replace, CancellationToken cancellationToken)
        {
            var updated = await replace;
            state.ReplaceRan = true;
            state.ReplacementId = updated.Id;
            if (state.HoldAfterReplace)
            {
                state.ReplacementFlushed.TrySetResult();
                await state.ReleaseReplacement.Task.WaitAsync(cancellationToken);
            }
            if (state.ThrowAfterReplace)
                throw new InvalidOperationException("injected after replace");
            return updated;
        }
    }

    private sealed class FaultingUnitOfWork(IUnitOfWork inner, FaultState state) : IUnitOfWork
    {
        public async Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            new FaultingTransaction(await inner.BeginTransactionAsync(cancellationToken), state);

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (state.ThrowOnSave)
                throw new InvalidOperationException("injected on save");
            await inner.SaveChangesAsync(cancellationToken);
        }

        public void DiscardChanges() => inner.DiscardChanges();
    }

    private sealed class FaultingTransaction(IWorkflowTransaction inner, FaultState state) : IWorkflowTransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (state.ThrowOnCommit)
                throw new InvalidOperationException("injected before commit");
            await inner.CommitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
