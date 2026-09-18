using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdvancedVariableFilterAuthorizationPostgresTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task LogicalFilterIsConjoinedWithEveryMandatoryRepositoryScope()
    {
        var seed = await SeedAuthorizationMatrixAsync();
        var filter = ParseFilter(
            """
            {
              "$or": [
                { "searchScope": { "$eq": "match" } },
                { "$not": { "searchScope": { "$ne": "match" } } }
              ]
            }
            """);

        await using var context = fixture.CreateDbContext();
        var runtime = new WorkflowRuntimeRepository(context);

        var visibleInstances = await runtime.ListInstancesAsync(
            status: null,
            instanceId: null,
            workflowId: null,
            workflowKey: seed.PrimaryWorkflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            variableFilter: filter,
            sort:
            [
                new InstanceSortCriterion(
                    InstanceSortField.Id,
                    SortDirection.Ascending)
            ],
            authorization: new InstanceListAuthorization(
                IsGlobalReader: false,
                LowerCallerRoles: [seed.ManagerRole.ToLowerInvariant()]),
            cursor: null,
            includeVariables: false,
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            visibleInstances.TotalCount,
            visibleInstances.Items.Select(item => item.Id),
            seed.AllowedMatchingInstanceId);

        var inbox = await runtime.ListInboxAsync(
            visibilityContext: new InboxVisibilityEvaluationContext(
                "medical-center-user",
                [seed.ActorRole],
                DateTimeOffset.UtcNow.AddMinutes(1),
                new Dictionary<string, JsonElement>()),
            instanceId: null,
            workflowId: null,
            workflowKey: seed.PrimaryWorkflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            variableFilter: filter,
            sort:
            [
                new InboxSortCriterion(
                    InboxSortField.UserTaskId,
                    SortDirection.Ascending)
            ],
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            inbox.TotalCount,
            inbox.Items.Select(item => item.UserTaskId),
            seed.AllowedMatchingTaskId);

        var manageable = await runtime.ListManageableUserTasksAsync(
            managerRoles: [seed.ManagerRole],
            taskId: null,
            instanceId: null,
            workflowId: null,
            workflowKey: seed.PrimaryWorkflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner: null,
            ownership: null,
            variableFilter: filter,
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            manageable.TotalCount,
            manageable.Items.Select(item => item.UserTaskId),
            seed.AllowedMatchingTaskId);

        var distributable = await runtime.ListDistributableUserTasksAsync(
            workflowKey: seed.PrimaryWorkflowKey,
            taskId: null,
            instanceId: null,
            workflowId: null,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner: null,
            ownership: null,
            variableFilter: filter,
            includeVariables: false,
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            distributable.TotalCount,
            distributable.Items.Select(item => item.UserTaskId),
            seed.AllowedMatchingTaskId,
            seed.HiddenVersionMatchingTaskId);

        var nodeQuery = new NodeExecutionQuery
        {
            WorkflowKey = seed.PrimaryWorkflowKey,
            NodeTypes = [],
            Statuses = [],
            InstanceStatuses = [],
            CompletionReasons = [],
            VariableFilter = filter,
            Sort =
            [
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.Id,
                    SortDirection.Ascending)
            ],
            Page = 1,
            PageSize = 50
        };
        var executions = await new NodeExecutionQueryRepository(context).SearchAsync(
            nodeQuery,
            new NodeExecutionAuthorization(
                IsGlobalReader: false,
                LowerCallerRoles: [seed.ManagerRole.ToLowerInvariant()]),
            CancellationToken.None);
        AssertIds(
            executions.TotalCount,
            executions.Items.Select(item => item.Id),
            seed.AllowedMatchingExecutionId);

        // These rows prove what each mandatory scope kept out. The matching
        // hidden-version row passes the filter but fails role visibility; the
        // other-family row passes the filter and roles but fails family scope;
        // the allowed blocked row passes every scope but fails the filter.
        Assert.DoesNotContain(
            seed.HiddenVersionMatchingInstanceId,
            visibleInstances.Items.Select(item => item.Id));
        Assert.DoesNotContain(
            seed.OtherFamilyMatchingTaskId,
            distributable.Items.Select(item => item.UserTaskId));
        Assert.DoesNotContain(
            seed.AllowedBlockedExecutionId,
            executions.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(OwnershipCase.AllTasks)]
    [InlineData(OwnershipCase.OwnedByTrimmedMixedCase)]
    [InlineData(OwnershipCase.Assigned)]
    [InlineData(OwnershipCase.ClaimedOnly)]
    [InlineData(OwnershipCase.Unowned)]
    [InlineData(OwnershipCase.AssignedWithOwner)]
    [InlineData(OwnershipCase.ClaimedWithOwner)]
    [InlineData(OwnershipCase.OwnerMismatch)]
    [InlineData(OwnershipCase.OwnerWithUnowned)]
    public async Task TaskOwnershipFiltersPreserveManagerAndDistributionMembership(
        OwnershipCase ownershipCase)
    {
        var seed = await SeedOwnershipMatrixAsync();
        var (owner, ownership, expectedIds) = ownershipCase switch
        {
            OwnershipCase.AllTasks => ((string?)null, (string?)null, new[]
            {
                seed.AssignedOnlyTaskId,
                seed.ClaimedOnlyTaskId,
                seed.UnownedTaskId,
                seed.BothOwnerTaskId
            }),
            OwnershipCase.OwnedByTrimmedMixedCase => ("  cAsE oWnEr  ", (string?)null, new[]
            {
                seed.AssignedOnlyTaskId,
                seed.ClaimedOnlyTaskId,
                seed.BothOwnerTaskId
            }),
            OwnershipCase.Assigned => ((string?)null, UserTaskOwnershipKinds.Assigned, new[]
            {
                seed.AssignedOnlyTaskId,
                seed.BothOwnerTaskId
            }),
            OwnershipCase.ClaimedOnly => ((string?)null, UserTaskOwnershipKinds.Claimed,
                new[] { seed.ClaimedOnlyTaskId }),
            OwnershipCase.Unowned => ((string?)null, UserTaskOwnershipKinds.Unassigned,
                new[] { seed.UnownedTaskId }),
            OwnershipCase.AssignedWithOwner => ("  CASE OWNER  ", UserTaskOwnershipKinds.Assigned,
                new[]
                {
                    seed.AssignedOnlyTaskId,
                    seed.BothOwnerTaskId
                }),
            OwnershipCase.ClaimedWithOwner => ("case owner", UserTaskOwnershipKinds.Claimed,
                new[] { seed.ClaimedOnlyTaskId }),
            OwnershipCase.OwnerMismatch => ("nobody-here", (string?)null, Array.Empty<long>()),
            OwnershipCase.OwnerWithUnowned => ("case owner", UserTaskOwnershipKinds.Unassigned,
                Array.Empty<long>()),
            _ => throw new ArgumentOutOfRangeException(nameof(ownershipCase))
        };

        await using var context = fixture.CreateDbContext();
        var runtime = new WorkflowRuntimeRepository(context);

        var manageable = await runtime.ListManageableUserTasksAsync(
            managerRoles: [seed.ManagerRole],
            taskId: null,
            instanceId: null,
            workflowId: null,
            workflowKey: seed.PrimaryWorkflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner,
            ownership,
            variableFilter: null,
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            manageable.TotalCount,
            manageable.Items.Select(item => item.UserTaskId),
            expectedIds);

        var distributable = await runtime.ListDistributableUserTasksAsync(
            workflowKey: seed.PrimaryWorkflowKey,
            taskId: null,
            instanceId: null,
            workflowId: null,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner,
            ownership,
            variableFilter: null,
            includeVariables: false,
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            distributable.TotalCount,
            distributable.Items.Select(item => item.UserTaskId),
            expectedIds);

        Assert.DoesNotContain(
            seed.OtherFamilyTaskId,
            manageable.Items.Select(item => item.UserTaskId));
        Assert.DoesNotContain(
            seed.OtherFamilyTaskId,
            distributable.Items.Select(item => item.UserTaskId));

        // The second workflow's tasks remain scoped to their own manager role.
        var otherOnly = await runtime.ListManageableUserTasksAsync(
            managerRoles: [seed.OtherManagerRole],
            taskId: null,
            instanceId: null,
            workflowId: null,
            workflowKey: null,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner: null,
            ownership: null,
            variableFilter: null,
            page: 1,
            pageSize: 50,
            CancellationToken.None);
        AssertIds(
            otherOnly.TotalCount,
            otherOnly.Items.Select(item => item.UserTaskId),
            seed.OtherFamilyTaskId);

        if (expectedIds.Length <= 1)
        {
            return;
        }

        var expectedOrder = seed.PrimaryTasks
            .Where(task => expectedIds.Contains(task.Id))
            .OrderByDescending(task => task.UpdatedAt)
            .ThenByDescending(task => task.Id)
            .Select(task => task.Id)
            .ToArray();
        for (var page = 1; page <= expectedIds.Length; page++)
        {
            var manageableSingle = await runtime.ListManageableUserTasksAsync(
                managerRoles: [seed.ManagerRole],
                taskId: null,
                instanceId: null,
                workflowId: null,
                workflowKey: seed.PrimaryWorkflowKey,
                businessKey: null,
                nodeId: null,
                nodeExternalId: null,
                owner,
                ownership,
                variableFilter: null,
                page,
                pageSize: 1,
                CancellationToken.None);
            Assert.Equal(expectedIds.Length, manageableSingle.TotalCount);
            Assert.Equal(
                expectedOrder[page - 1],
                Assert.Single(manageableSingle.Items).UserTaskId);

            var distributableSingle = await runtime.ListDistributableUserTasksAsync(
                workflowKey: seed.PrimaryWorkflowKey,
                taskId: null,
                instanceId: null,
                workflowId: null,
                businessKey: null,
                nodeId: null,
                nodeExternalId: null,
                owner,
                ownership,
                variableFilter: null,
                includeVariables: false,
                page,
                pageSize: 1,
                CancellationToken.None);
            Assert.Equal(expectedIds.Length, distributableSingle.TotalCount);
            Assert.Equal(
                expectedOrder[page - 1],
                Assert.Single(distributableSingle.Items).UserTaskId);
        }
    }

    [Fact]
    public async Task LegacyGetAstAndAdvancedEqIgnoreCaseHaveLatestScalarParity()
    {
        var seed = await SeedAuthorizationMatrixAsync();
        var legacy = VariableFilterParser.FromLegacy(
            [new VariableFilter("legacyState", "current")]);
        var advanced = ParseFilter(
            """{ "legacyState": { "$eqIgnoreCase": "current" } }""");

        Assert.NotNull(legacy);
        await using var context = fixture.CreateDbContext();
        var repository = new WorkflowRuntimeRepository(context);
        var legacyResult = await ListInstancesAsync(
            repository,
            seed.PrimaryWorkflowKey,
            legacy);
        var advancedResult = await ListInstancesAsync(
            repository,
            seed.PrimaryWorkflowKey,
            advanced);

        var expected = new[]
        {
            seed.AllowedMatchingInstanceId,
            seed.AllowedBlockedInstanceId
        };
        AssertIds(
            legacyResult.TotalCount,
            legacyResult.Items.Select(item => item.Id),
            expected);
        AssertIds(
            advancedResult.TotalCount,
            advancedResult.Items.Select(item => item.Id),
            expected);
        Assert.Equal(
            legacyResult.Items.Select(item => item.Id),
            advancedResult.Items.Select(item => item.Id));

        var expectedTasks = new[]
        {
            seed.AllowedMatchingTaskId,
            seed.AllowedBlockedTaskId
        };
        var inboxAsOf = DateTimeOffset.UtcNow.AddMinutes(1);
        var legacyInbox = await ListInboxAsync(
            repository,
            seed,
            legacy,
            inboxAsOf);
        var advancedInbox = await ListInboxAsync(
            repository,
            seed,
            advanced,
            inboxAsOf);
        AssertParity(
            legacyInbox,
            advancedInbox,
            item => item.UserTaskId,
            expectedTasks);

        var legacyManageable = await ListManageableAsync(
            repository,
            seed,
            legacy);
        var advancedManageable = await ListManageableAsync(
            repository,
            seed,
            advanced);
        AssertParity(
            legacyManageable,
            advancedManageable,
            item => item.UserTaskId,
            expectedTasks);

        var legacyDistributable = await ListDistributableAsync(
            repository,
            seed,
            legacy);
        var advancedDistributable = await ListDistributableAsync(
            repository,
            seed,
            advanced);
        AssertParity(
            legacyDistributable,
            advancedDistributable,
            item => item.UserTaskId,
            expectedTasks);

        var expectedExecutions = new[]
        {
            seed.AllowedMatchingExecutionId,
            seed.AllowedBlockedExecutionId
        };
        var nodeRepository = new NodeExecutionQueryRepository(context);
        var legacyExecutions = await ListNodeExecutionsAsync(
            nodeRepository,
            seed,
            legacy);
        var advancedExecutions = await ListNodeExecutionsAsync(
            nodeRepository,
            seed,
            advanced);
        AssertParity(
            legacyExecutions,
            advancedExecutions,
            item => item.Id,
            expectedExecutions);

        // The first instance originally held "old"; only its later "Current"
        // projection row is searchable through either compatibility path.
        var stale = await ListInstancesAsync(
            repository,
            seed.PrimaryWorkflowKey,
            VariableFilterParser.FromLegacy(
                [new VariableFilter("legacyState", "old")]));
        Assert.Equal(0, stale.TotalCount);
        Assert.Empty(stale.Items);
    }

    private static Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        WorkflowRuntimeRepository repository,
        string workflowKey,
        VariableFilterExpression? variableFilter) =>
        repository.ListInstancesAsync(
            status: null,
            instanceId: null,
            workflowId: null,
            workflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            variableFilter,
            sort:
            [
                new InstanceSortCriterion(
                    InstanceSortField.Id,
                    SortDirection.Ascending)
            ],
            authorization: InstanceListAuthorization.Global,
            cursor: null,
            includeVariables: false,
            page: 1,
            pageSize: 50,
            CancellationToken.None);

    private static Task<PagedResult<InboxListItem>> ListInboxAsync(
        WorkflowRuntimeRepository repository,
        AuthorizationMatrixSeed seed,
        VariableFilterExpression? variableFilter,
        DateTimeOffset asOf) =>
        repository.ListInboxAsync(
            visibilityContext: new InboxVisibilityEvaluationContext(
                "medical-center-user",
                [seed.ActorRole],
                asOf,
                new Dictionary<string, JsonElement>()),
            instanceId: null,
            workflowId: null,
            workflowKey: seed.PrimaryWorkflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            variableFilter,
            sort:
            [
                new InboxSortCriterion(
                    InboxSortField.UserTaskId,
                    SortDirection.Ascending)
            ],
            page: 1,
            pageSize: 50,
            CancellationToken.None);

    private static Task<PagedResult<ManagedUserTaskRecord>> ListManageableAsync(
        WorkflowRuntimeRepository repository,
        AuthorizationMatrixSeed seed,
        VariableFilterExpression? variableFilter) =>
        repository.ListManageableUserTasksAsync(
            managerRoles: [seed.ManagerRole],
            taskId: null,
            instanceId: null,
            workflowId: null,
            workflowKey: seed.PrimaryWorkflowKey,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner: null,
            ownership: null,
            variableFilter,
            page: 1,
            pageSize: 50,
            CancellationToken.None);

    private static Task<PagedResult<ManagedUserTaskRecord>> ListDistributableAsync(
        WorkflowRuntimeRepository repository,
        AuthorizationMatrixSeed seed,
        VariableFilterExpression? variableFilter) =>
        repository.ListDistributableUserTasksAsync(
            workflowKey: seed.PrimaryWorkflowKey,
            taskId: null,
            instanceId: null,
            workflowId: null,
            businessKey: null,
            nodeId: null,
            nodeExternalId: null,
            owner: null,
            ownership: null,
            variableFilter,
            includeVariables: false,
            page: 1,
            pageSize: 50,
            CancellationToken.None);

    private static Task<PagedResult<NodeExecutionSummaryDto>> ListNodeExecutionsAsync(
        NodeExecutionQueryRepository repository,
        AuthorizationMatrixSeed seed,
        VariableFilterExpression? variableFilter) =>
        repository.SearchAsync(
            new NodeExecutionQuery
            {
                WorkflowKey = seed.PrimaryWorkflowKey,
                NodeTypes = [],
                Statuses = [],
                InstanceStatuses = [],
                CompletionReasons = [],
                VariableFilter = variableFilter,
                Sort =
                [
                    new NodeExecutionSortCriterion(
                        NodeExecutionSortField.Id,
                        SortDirection.Ascending)
                ],
                Page = 1,
                PageSize = 50
            },
            new NodeExecutionAuthorization(
                IsGlobalReader: false,
                LowerCallerRoles: [seed.ManagerRole.ToLowerInvariant()]),
            CancellationToken.None);

    private async Task<AuthorizationMatrixSeed> SeedAuthorizationMatrixAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var primaryWorkflowKey = $"authorization-primary-{suffix}";
        var otherWorkflowKey = $"authorization-other-{suffix}";
        var managerRole = $"center-manager-{suffix}";
        var hiddenManagerRole = $"other-manager-{suffix}";
        var actorRole = $"center-user-{suffix}";
        var hiddenActorRole = $"other-user-{suffix}";
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var setup = fixture.CreateDbContext();
        var allowedDefinition = Definition(
            primaryWorkflowKey,
            version: 1,
            name: "Allowed version",
            managerRole);
        var hiddenVersionDefinition = Definition(
            primaryWorkflowKey,
            version: 2,
            name: "Hidden version",
            hiddenManagerRole);
        var otherFamilyDefinition = Definition(
            otherWorkflowKey,
            version: 1,
            name: "Other family",
            managerRole);
        setup.WorkflowDefinitions.AddRange(
            allowedDefinition,
            hiddenVersionDefinition,
            otherFamilyDefinition);
        await setup.SaveChangesAsync();

        var allowedMatching = Instance(
            allowedDefinition,
            primaryWorkflowKey,
            now);
        var allowedBlocked = Instance(
            allowedDefinition,
            primaryWorkflowKey,
            now.AddSeconds(1));
        var hiddenVersionMatching = Instance(
            hiddenVersionDefinition,
            primaryWorkflowKey,
            now.AddSeconds(2));
        var otherFamilyMatching = Instance(
            otherFamilyDefinition,
            otherWorkflowKey,
            now.AddSeconds(3));
        var instances = new[]
        {
            allowedMatching,
            allowedBlocked,
            hiddenVersionMatching,
            otherFamilyMatching
        };
        setup.WorkflowInstances.AddRange(instances);
        await setup.SaveChangesAsync();

        var tokens = instances
            .Select((instance, index) => Token(instance, now.AddSeconds(index)))
            .ToArray();
        setup.ExecutionTokens.AddRange(tokens);
        await setup.SaveChangesAsync();

        var tasks = new[]
        {
            Task(allowedMatching, tokens[0], actorRole, now),
            Task(allowedBlocked, tokens[1], actorRole, now.AddSeconds(1)),
            Task(hiddenVersionMatching, tokens[2], hiddenActorRole, now.AddSeconds(2)),
            Task(otherFamilyMatching, tokens[3], actorRole, now.AddSeconds(3))
        };
        setup.UserTasks.AddRange(tasks);
        await setup.SaveChangesAsync();

        var executions = instances
            .Select((instance, index) => Execution(
                instance,
                tokens[index],
                tasks[index],
                now.AddSeconds(index)))
            .ToArray();
        setup.NodeExecutions.AddRange(executions);
        await setup.SaveChangesAsync();

        for (var index = 0; index < tokens.Length; index++)
        {
            tokens[index].CurrentNodeExecutionId = executions[index].Id;
        }
        await setup.SaveChangesAsync();

        setup.InstanceVariables.AddRange(
            Variable(allowedMatching.Id, "searchScope", "match", now),
            Variable(allowedBlocked.Id, "searchScope", "blocked", now.AddSeconds(1)),
            Variable(hiddenVersionMatching.Id, "searchScope", "match", now.AddSeconds(2)),
            Variable(otherFamilyMatching.Id, "searchScope", "match", now.AddSeconds(3)),
            Variable(allowedMatching.Id, "legacyState", "old", now),
            Variable(allowedBlocked.Id, "legacyState", "CURRENT", now.AddSeconds(1)),
            Variable(hiddenVersionMatching.Id, "legacyState", "other", now.AddSeconds(2)),
            Variable(otherFamilyMatching.Id, "legacyState", "CURRENT", now.AddSeconds(3)));
        await setup.SaveChangesAsync();

        setup.InstanceVariables.Add(
            Variable(
                allowedMatching.Id,
                "legacyState",
                "Current",
                now.AddMinutes(1)));
        await setup.SaveChangesAsync();

        return new AuthorizationMatrixSeed(
            primaryWorkflowKey,
            otherWorkflowKey,
            managerRole,
            actorRole,
            allowedMatching.Id,
            allowedBlocked.Id,
            hiddenVersionMatching.Id,
            otherFamilyMatching.Id,
            tasks[0].Id,
            tasks[1].Id,
            tasks[2].Id,
            tasks[3].Id,
            executions[0].Id,
            executions[1].Id,
            executions[2].Id,
            executions[3].Id);
    }

    private async Task<OwnershipMatrixSeed> SeedOwnershipMatrixAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var primaryWorkflowKey = $"ownership-primary-{suffix}";
        var otherWorkflowKey = $"ownership-other-{suffix}";
        var managerRole = $"ownership-manager-{suffix}";
        var otherManagerRole = $"ownership-other-manager-{suffix}";
        var actorRole = $"ownership-user-{suffix}";
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var setup = fixture.CreateDbContext();
        var primaryDefinition = Definition(
            primaryWorkflowKey,
            version: 1,
            name: "Ownership primary",
            managerRole);
        var otherDefinition = Definition(
            otherWorkflowKey,
            version: 1,
            name: "Ownership other",
            otherManagerRole);
        setup.WorkflowDefinitions.AddRange(primaryDefinition, otherDefinition);
        await setup.SaveChangesAsync();

        var primaryInstances = new[]
        {
            Instance(primaryDefinition, primaryWorkflowKey, now),
            Instance(primaryDefinition, primaryWorkflowKey, now.AddSeconds(1)),
            Instance(primaryDefinition, primaryWorkflowKey, now.AddSeconds(2)),
            Instance(primaryDefinition, primaryWorkflowKey, now.AddSeconds(3))
        };
        var otherInstance = Instance(otherDefinition, otherWorkflowKey, now.AddSeconds(4));
        setup.WorkflowInstances.AddRange(primaryInstances);
        setup.WorkflowInstances.Add(otherInstance);
        await setup.SaveChangesAsync();

        var tokens = primaryInstances
            .Select((instance, index) => Token(instance, now.AddSeconds(index)))
            .Append(Token(otherInstance, now.AddSeconds(4)))
            .ToArray();
        setup.ExecutionTokens.AddRange(tokens);
        await setup.SaveChangesAsync();

        // Distinct UpdatedAt values keep the page ordering deterministic. The
        // both-owner task proves assignee precedence: the owner filter matches
        // through it, it counts as assigned, and it never counts as claimed.
        var assignedOnly = Task(primaryInstances[0], tokens[0], actorRole, now);
        assignedOnly.Assignee = "Case Owner";
        var claimedOnly = Task(primaryInstances[1], tokens[1], actorRole, now.AddSeconds(1));
        claimedOnly.ClaimedBy = "Case Owner";
        var unowned = Task(primaryInstances[2], tokens[2], actorRole, now.AddSeconds(2));
        var bothOwner = Task(primaryInstances[3], tokens[3], actorRole, now.AddSeconds(3));
        bothOwner.Assignee = "Case Owner";
        bothOwner.ClaimedBy = "Case Holder";
        var otherFamily = Task(otherInstance, tokens[4], actorRole, now.AddSeconds(4));
        otherFamily.Assignee = "Case Owner";
        var primaryTasks = new[]
        {
            assignedOnly,
            claimedOnly,
            unowned,
            bothOwner
        };
        setup.UserTasks.AddRange(primaryTasks);
        setup.UserTasks.Add(otherFamily);
        await setup.SaveChangesAsync();

        return new OwnershipMatrixSeed(
            primaryWorkflowKey,
            managerRole,
            otherManagerRole,
            assignedOnly.Id,
            claimedOnly.Id,
            unowned.Id,
            bothOwner.Id,
            otherFamily.Id,
            primaryTasks);
    }

    private static WorkflowDefinitionEntity Definition(
        string workflowKey,
        int version,
        string name,
        string managerRole) => new()
        {
            Name = name,
            WorkflowKey = workflowKey,
            Version = version,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = name,
                TaskAssignmentRoles = [managerRole],
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = 2,
                        Name = "Review",
                        Type = BpmnFlowNodeTypes.UserTask
                    }
                ]
            }
        };

    private static WorkflowInstanceEntity Instance(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        DateTimeOffset createdAt) => new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = "authorization-test",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static ExecutionTokenEntity Token(
        WorkflowInstanceEntity instance,
        DateTimeOffset createdAt) => new()
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeExternalId = "review",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static UserTaskEntity Task(
        WorkflowInstanceEntity instance,
        ExecutionTokenEntity token,
        string actorRole,
        DateTimeOffset createdAt) => new()
        {
            InstanceId = instance.Id,
            TokenId = token.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeExternalId = "review",
            Roles = [actorRole],
            RequiresClaim = false,
            RequiresAssignment = false,
            Status = UserTaskStatuses.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static NodeExecutionEntity Execution(
        WorkflowInstanceEntity instance,
        ExecutionTokenEntity token,
        UserTaskEntity task,
        DateTimeOffset createdAt) => new()
        {
            InstanceId = instance.Id,
            WorkflowDefinitionId = instance.WorkflowDefinitionId,
            ExecutionTokenId = token.Id,
            UserTaskId = task.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeExternalId = "review",
            NodeType = BpmnFlowNodeTypes.UserTask,
            ExecutionKind = NodeExecutionKinds.Node,
            Status = NodeExecutionStatuses.Active,
            NodeRolesJson = JsonDocument.Parse(
                JsonSerializer.Serialize(task.Roles)),
            TriggeredBy = "authorization-test",
            TriggeredByRolesJson = JsonDocument.Parse("[]"),
            CreatedAt = createdAt,
            StartedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static InstanceVariableEntity Variable(
        long instanceId,
        string name,
        object? value,
        DateTimeOffset setAt) => new()
        {
            InstanceId = instanceId,
            VariableName = name,
            ValueJson = JsonDocument.Parse(JsonSerializer.Serialize(value)),
            SetBy = "authorization-test",
            SetAt = setAt
        };

    private static VariableFilterExpression ParseFilter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var expression = VariableFilterParser.Parse(document.RootElement);
        Assert.NotNull(expression);
        return expression!;
    }

    private static void AssertIds(
        long totalCount,
        IEnumerable<long> actual,
        params long[] expected)
    {
        var actualIds = actual.OrderBy(id => id).ToArray();
        var expectedIds = expected.OrderBy(id => id).ToArray();
        Assert.Equal(expectedIds.Length, totalCount);
        Assert.Equal(expectedIds, actualIds);
    }

    private static void AssertParity<T>(
        PagedResult<T> legacy,
        PagedResult<T> advanced,
        Func<T, long> idSelector,
        params long[] expected)
    {
        AssertIds(
            legacy.TotalCount,
            legacy.Items.Select(idSelector),
            expected);
        AssertIds(
            advanced.TotalCount,
            advanced.Items.Select(idSelector),
            expected);
        Assert.Equal(
            legacy.Items.Select(idSelector),
            advanced.Items.Select(idSelector));
    }

    private sealed record AuthorizationMatrixSeed(
        string PrimaryWorkflowKey,
        string OtherWorkflowKey,
        string ManagerRole,
        string ActorRole,
        long AllowedMatchingInstanceId,
        long AllowedBlockedInstanceId,
        long HiddenVersionMatchingInstanceId,
        long OtherFamilyMatchingInstanceId,
        long AllowedMatchingTaskId,
        long AllowedBlockedTaskId,
        long HiddenVersionMatchingTaskId,
        long OtherFamilyMatchingTaskId,
        long AllowedMatchingExecutionId,
        long AllowedBlockedExecutionId,
        long HiddenVersionMatchingExecutionId,
        long OtherFamilyMatchingExecutionId);

    public enum OwnershipCase
    {
        AllTasks,
        OwnedByTrimmedMixedCase,
        Assigned,
        ClaimedOnly,
        Unowned,
        AssignedWithOwner,
        ClaimedWithOwner,
        OwnerMismatch,
        OwnerWithUnowned
    }

    private sealed record OwnershipMatrixSeed(
        string PrimaryWorkflowKey,
        string ManagerRole,
        string OtherManagerRole,
        long AssignedOnlyTaskId,
        long ClaimedOnlyTaskId,
        long UnownedTaskId,
        long BothOwnerTaskId,
        long OtherFamilyTaskId,
        IReadOnlyList<UserTaskEntity> PrimaryTasks);
}
