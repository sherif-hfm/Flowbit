using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Flowbit.Infrastructure.DependencyInjection;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.DependencyInjection;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowInstanceQueryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredRolesAreReadAgainForEachQueryAndNormalized(bool advanced)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new QueryFixture(0, 0, cancellation.Token);
        var admin = new ActorContext("admin-reader", [" ADMIN ", "admin", " "], new Dictionary<string, string>());
        var auditor = new ActorContext("auditor", [" AuDiToR ", "AUDITOR", " "], new Dictionary<string, string>());

        // Reuse the same service to catch settings cached for its scoped lifetime.
        foreach (var setting in new string?[] { null, "", "   ", " , , " })
        {
            fixture.RequiredRoles = setting;
            await QueryAsync(fixture.Service, advanced, admin, false, cancellation.Token);
            Assert.True(fixture.Authorizations[^1].IsGlobalReader);
            Assert.Equal(["admin"], fixture.Authorizations[^1].LowerCallerRoles);
            await QueryAsync(fixture.Service, advanced, auditor, false, cancellation.Token);
            Assert.False(fixture.Authorizations[^1].IsGlobalReader);
        }

        fixture.RequiredRoles = " finance, AuDiToR, auditor, , ";
        await QueryAsync(fixture.Service, advanced, admin, false, cancellation.Token);
        Assert.False(fixture.Authorizations[^1].IsGlobalReader);
        await QueryAsync(fixture.Service, advanced, auditor, false, cancellation.Token);
        Assert.True(fixture.Authorizations[^1].IsGlobalReader);
        Assert.Equal(["auditor"], fixture.Authorizations[^1].LowerCallerRoles);

        fixture.RequiredRoles = "finance";
        await QueryAsync(fixture.Service, advanced, auditor, false, cancellation.Token);
        Assert.False(fixture.Authorizations[^1].IsGlobalReader);
        Assert.Equal(11, fixture.SettingsReads);
        Assert.Equal(11, fixture.Authorizations.Count);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(false, 1, 1)]
    [InlineData(true, 1, 1)]
    [InlineData(false, 40, 1)]
    [InlineData(true, 40, 1)]
    [InlineData(false, 40, 4)]
    [InlineData(true, 40, 4)]
    public async Task EnrichmentIsBoundedByTheSelectedPageAndDistinctWorkflows(
        bool advanced, int itemCount, int workflowCount)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new QueryFixture(itemCount, workflowCount, cancellation.Token);
        var originalRows = JsonSerializer.Serialize(fixture.Page);
        var originalDefinitions = JsonSerializer.Serialize(fixture.Definitions);

        var result = await QueryAsync(fixture.Service, advanced, Admin, true, cancellation.Token);

        Assert.Single(fixture.Authorizations);
        Assert.Equal(fixture.Page.Items.Select(row => row.Id), Assert.Single(fixture.JobBatches));
        Assert.Equal(fixture.Page.Items.Select(row => row.WorkflowId).Distinct(),
            Assert.Single(fixture.DefinitionBatches));
        Assert.Equal(workflowCount, fixture.BindingReads.Count);
        Assert.Equal(fixture.Definitions.Keys.Order(), fixture.BindingReads.Order());
        Assert.Equal(fixture.Page.Items.Select(row => row.Id), result.Items.Select(row => row.Id));
        Assert.Equal(fixture.Page.Page, result.Page);
        Assert.Equal(fixture.Page.PageSize, result.PageSize);
        Assert.Equal(fixture.Page.TotalCount, result.TotalCount);
        Assert.Equal(fixture.Page.NextCursor, result.NextCursor);

        foreach (var item in result.Items)
        {
            var row = fixture.Page.Items.Single(row => row.Id == item.Id);
            Assert.Equal(row.Variables, item.Variables);
            Assert.Equal(fixture.Bindings[row.WorkflowId], item.SharedVariables);
            if (fixture.Jobs.TryGetValue(item.Id, out var job))
            {
                Assert.Equal(new InstanceJobSummaryDto(job.OpenCount, job.QueuedCount,
                    job.RunningCount, job.IncidentCount, job.NearestDueAt), item.Jobs);
            }
            else
            {
                Assert.Null(item.Jobs);
            }
        }
        Assert.Equal(originalRows, JsonSerializer.Serialize(fixture.Page));
        Assert.Equal(originalDefinitions, JsonSerializer.Serialize(fixture.Definitions));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task SharedBindingsAreNotLoadedWhenOmittedOrTheStoreIsUnavailable(
        bool advanced, bool includeVariables, bool hasVariableStore)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new QueryFixture(3, 2, cancellation.Token, hasVariableStore);

        var result = await QueryAsync(fixture.Service, advanced, Admin, includeVariables, cancellation.Token);

        Assert.Equal(3, result.Items.Count);
        Assert.Single(fixture.JobBatches);
        Assert.Empty(fixture.DefinitionBatches);
        Assert.Empty(fixture.BindingReads);
        Assert.All(result.Items, item => Assert.Null(item.SharedVariables));
    }

    [Fact]
    public async Task ProductionRegistrationsShareRepositoriesWithinEachScopeAndResolveTheEngine()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Flowbit"] =
                    "Host=127.0.0.1;Port=1;Database=query_composition;Username=test;Password=test"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowContextOptions());
        services.AddServiceLayer();
        services.AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var firstQuery = VerifyScope(first.ServiceProvider);
        var secondQuery = VerifyScope(second.ServiceProvider);
        Assert.NotSame(firstQuery, secondQuery);
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IWorkflowInstanceQueryRepository>(),
            second.ServiceProvider.GetRequiredService<IWorkflowInstanceQueryRepository>());

        static IWorkflowInstanceQueryService VerifyScope(IServiceProvider scope)
        {
            var repository = scope.GetRequiredService<WorkflowRuntimeRepository>();
            Assert.Same(repository, scope.GetRequiredService<IWorkflowRuntimeRepository>());
            Assert.Same(repository, scope.GetRequiredService<IWorkflowInstanceQueryRepository>());
            var query = scope.GetRequiredService<IWorkflowInstanceQueryService>();
            Assert.IsType<WorkflowInstanceQueryService>(query);
            Assert.Same(query, scope.GetRequiredService<IWorkflowInstanceQueryService>());
            var engine = scope.GetRequiredService<WorkflowEngineService>();
            Assert.Same(engine, scope.GetRequiredService<IWorkflowEngineService>());
            Assert.Same(engine, scope.GetRequiredService<IConditionalEventRuntimeCoordinator>());
            Assert.Same(engine, scope.GetRequiredService<IAdministrativeActionExecutor>());
            Assert.Same(engine, scope.GetRequiredService<IInstanceVersionChangeBatchExecutor>());
            return query;
        }
    }

    private static readonly ActorContext Admin = new("reader", ["admin"], new Dictionary<string, string>());

    private static Task<PagedResult<InstanceSummaryDto>> QueryAsync(
        IWorkflowInstanceQueryService service, bool advanced, ActorContext actor,
        bool includeVariables, CancellationToken cancellationToken) =>
        advanced
            ? service.SearchInstancesAsync(actor, new InstanceSearchRequest
                { IncludeVariables = includeVariables, PageSize = 50 }, cancellationToken)
            : service.ListInstancesAsync(actor, null, null, null, null, null, null, null,
                null, null, null, includeVariables, 1, 50, cancellationToken);

    private sealed class QueryFixture
    {
        public string? RequiredRoles { get; set; }
        public int SettingsReads { get; private set; }
        public List<InstanceListAuthorization> Authorizations { get; } = [];
        public List<long[]> JobBatches { get; } = [];
        public List<long[]> DefinitionBatches { get; } = [];
        public List<long> BindingReads { get; } = [];
        public Dictionary<long, WorkflowDefinitionRecord> Definitions { get; } = [];
        public Dictionary<long, IReadOnlyList<SharedVariableBindingMetadataDto>> Bindings { get; } = [];
        public Dictionary<long, WorkflowInstanceJobSummaryRecord> Jobs { get; } = [];
        public PagedResult<InstanceListItem> Page { get; }
        public WorkflowInstanceQueryService Service { get; }

        public QueryFixture(int itemCount, int workflowCount, CancellationToken token, bool hasVariableStore = true)
        {
            var timestamp = DateTimeOffset.UnixEpoch;
            for (var index = 0; index < workflowCount; index++)
            {
                var id = 10L + index;
                var definition = new WorkflowModel { Id = $"workflow-{id}", Name = $"Workflow {id}" };
                Definitions[id] = new WorkflowDefinitionRecord(id, definition.Name, definition.Id,
                    1, definition, true, true, timestamp);
                Bindings[id] = [new SharedVariableBindingMetadataDto($"alias-{id}", $"key-{id}",
                    "read", "string", false, true, null, "active", id, true, timestamp, timestamp, null)];
            }
            // Descending IDs make unintended reordering visible. Repeated workflows
            // distinguish per-workflow binding reads from per-instance reads.
            var rows = Enumerable.Range(0, itemCount).Select(index =>
            {
                var id = 100L + itemCount - index;
                var workflowId = 10L + index % workflowCount;
                if (index % 2 == 0)
                    Jobs[id] = new WorkflowInstanceJobSummaryRecord(id, 6, 3, 2, 1, timestamp.AddMinutes(id));
                return new InstanceListItem(id, workflowId, workflowId, $"Workflow {workflowId}",
                    1, null, null, id + 1000, null, null, null, null, null,
                    1, "Review", null, "userTask", [], false, false, "running", null, "author",
                    timestamp, timestamp, null,
                    new Dictionary<string, JsonElement> { ["amount"] = JsonSerializer.SerializeToElement(id) });
            }).ToArray();
            Page = new PagedResult<InstanceListItem>(rows, 1, 50, itemCount == 0 ? 0 : itemCount + 5)
                { NextCursor = itemCount == 0 ? null : "repository-next-cursor" };

            Service = new WorkflowInstanceQueryService(
                Stub<IWorkflowInstanceQueryRepository>(nameof(IWorkflowInstanceQueryRepository.ListInstancesAsync), args =>
                {
                    Authorizations.Add(Assert.IsType<InstanceListAuthorization>(args[9]));
                    return Task.FromResult(Page);
                }),
                Stub<IWorkflowDefinitionRepository>(nameof(IWorkflowDefinitionRepository.GetManyAsync), args =>
                {
                    var ids = Assert.IsAssignableFrom<IReadOnlyCollection<long>>(args[0]).ToArray();
                    DefinitionBatches.Add(ids);
                    return Task.FromResult<IReadOnlyDictionary<long, WorkflowDefinitionRecord>>(
                        ids.ToDictionary(id => id, id => Definitions[id]));
                }),
                Stub<IWorkflowJobRepository>(nameof(IWorkflowJobRepository.GetInstanceJobSummariesAsync), args =>
                {
                    JobBatches.Add(Assert.IsAssignableFrom<IReadOnlyCollection<long>>(args[0]).ToArray());
                    return Task.FromResult<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>>(Jobs);
                }),
                Stub<IEngineSettingsRepository>(nameof(IEngineSettingsRepository.GetByKeyAsync), args =>
                {
                    Assert.Equal("WorkflowInstances.RequiredRole", args[0]);
                    SettingsReads++;
                    return Task.FromResult<EngineSettingRecord?>(RequiredRoles is null ? null :
                        new EngineSettingRecord(1, null, "WorkflowInstances.RequiredRole", RequiredRoles, timestamp, timestamp));
                }),
                hasVariableStore
                    ? Stub<IWorkflowVariableStore>(nameof(IWorkflowVariableStore.DescribeBindingsAsync), args =>
                    {
                        var definition = Assert.IsType<WorkflowModel>(args[0]);
                        var id = Definitions.Single(pair => ReferenceEquals(pair.Value.Definition, definition)).Key;
                        BindingReads.Add(id);
                        return Task.FromResult(Bindings[id]);
                    })
                    : null);

            T Stub<T>(string expectedMethod, Func<object?[], object?> handler) where T : class
            {
                var proxy = DispatchProxy.Create<T, StrictProxy>();
                ((StrictProxy)(object)proxy).Handler = (method, args) =>
                {
                    Assert.Equal(expectedMethod, method.Name);
                    Assert.Equal(token, Assert.IsType<CancellationToken>(args[^1]));
                    return handler(args);
                };
                return proxy;
            }
        }
    }

    private class StrictProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
