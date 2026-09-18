using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Characterizes the retained engine compatibility members: instance list and
/// search orchestration lives in WorkflowInstanceQueryService, instance detail
/// projection lives in WorkflowInstanceProjectionService, and the engine
/// forwards the original interface members to those services unchanged.
/// </summary>
public sealed class WorkflowEngineInstanceQueryForwardingTests
{
    [Fact]
    public async Task ListInstancesForwardsEveryArgumentToTheQueryService()
    {
        var recorded = new List<RecordedCall>();
        var result = new PagedResult<InstanceSummaryDto>([], 2, 25, 7);
        var engine = CreateEngine(
            recorded,
            nameof(IWorkflowInstanceQueryService.ListInstancesAsync),
            () => result);
        var actor = new ActorContext("alice", ["admin"], new Dictionary<string, string>());
        var variables = new[] { "region:north" };
        var sort = new[] { "createdAt:asc" };

        var forwarded = await engine.ListInstancesAsync(
            actor,
            "running",
            11,
            12,
            "health-certificate",
            "HC-1",
            3,
            "MEDICAL_REVIEW",
            variables,
            sort,
            "cursor-token",
            includeVariables: true,
            page: 2,
            pageSize: 25,
            cancellationToken: CancellationToken.None);

        Assert.Same(result, forwarded);
        var call = Assert.Single(recorded);
        Assert.Equal(nameof(IWorkflowInstanceQueryService.ListInstancesAsync), call.Method);
        Assert.Equal(
            [
                actor, "running", 11L, 12L, "health-certificate", "HC-1", 3,
                "MEDICAL_REVIEW", variables, sort, "cursor-token", true, 2, 25,
                CancellationToken.None
            ],
            call.Arguments);
    }

    [Fact]
    public async Task SearchInstancesForwardsTheRequestToTheQueryService()
    {
        var recorded = new List<RecordedCall>();
        var result = new PagedResult<InstanceSummaryDto>([], 1, 50, 0);
        var engine = CreateEngine(
            recorded,
            nameof(IWorkflowInstanceQueryService.SearchInstancesAsync),
            () => result);
        var actor = new ActorContext("bob", [], new Dictionary<string, string>());
        var request = new InstanceSearchRequest { Status = "completed", Page = 1 };

        var forwarded = await engine.SearchInstancesAsync(actor, request, CancellationToken.None);

        Assert.Same(result, forwarded);
        var call = Assert.Single(recorded);
        Assert.Equal(nameof(IWorkflowInstanceQueryService.SearchInstancesAsync), call.Method);
        Assert.Equal([actor, request, CancellationToken.None], call.Arguments);
    }

    [Fact]
    public async Task GetInstanceForwardsToTheProjectionService()
    {
        var recorded = new List<RecordedCall>();
        var detail = new InstanceDetailDto(
            5,
            new WorkflowDetailDto(9, "Health", "health-certificate", 1, true, true,
                DateTimeOffset.UnixEpoch, new WorkflowModel()),
            3,
            "Review",
            "MEDICAL_REVIEW",
            "running",
            null,
            null,
            "alice",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [],
            [],
            null,
            null);
        var projectionService = Proxy<IWorkflowInstanceProjectionService>((method, arguments) =>
        {
            recorded.Add(new RecordedCall(method.Name, arguments));
            return Task.FromResult<InstanceDetailDto?>(detail);
        });
        var engine = CreateEngine(
            [],
            "unused",
            () => throw new InvalidOperationException("The query service must not be called."),
            projectionService);

        var forwarded = await engine.GetInstanceAsync(5, CancellationToken.None);

        Assert.Same(detail, forwarded);
        var call = Assert.Single(recorded);
        Assert.Equal(nameof(IWorkflowInstanceProjectionService.GetDetailAsync), call.Method);
        Assert.Equal([5L, CancellationToken.None], call.Arguments);
    }

    private static WorkflowEngineService CreateEngine(
        List<RecordedCall> recorded,
        string expectedMethod,
        Func<PagedResult<InstanceSummaryDto>> result,
        IWorkflowInstanceProjectionService? projectionService = null)
    {
        var queryService = Proxy<IWorkflowInstanceQueryService>((method, arguments) =>
        {
            if (method.Name == expectedMethod)
            {
                recorded.Add(new RecordedCall(method.Name, arguments));
                return Task.FromResult(result());
            }
            return Unexpected(method, arguments);
        });
        var runtimeProxy = Proxy<IWorkflowRuntimeRepository>(Unexpected);
        var definitionsProxy = Proxy<IWorkflowDefinitionRepository>(Unexpected);
        return new WorkflowEngineService(
            definitionsProxy,
            runtimeProxy,
            Proxy<IWorkflowJobRepository>(Unexpected),
            Proxy<ITimerSubscriptionRepository>(Unexpected),
            Proxy<IUserDelegationRepository>(Unexpected),
            Proxy<IUnitOfWork>(Unexpected),
            Proxy<IServiceTaskInvoker>(Unexpected),
            Proxy<IScriptEvaluator>(Unexpected),
            new WorkflowContextOptions(),
            TimeProvider.System,
            Proxy<IWorkflowSettingsRepository>(Unexpected),
            Proxy<IEngineSettingsRepository>(Unexpected),
            NullLogger<WorkflowEngineService>.Instance,
            queryService,
            projectionService
                ?? new WorkflowInstanceProjectionService(runtimeProxy, definitionsProxy));
    }

    private sealed record RecordedCall(string Method, IReadOnlyList<object?> Arguments);

    private class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = Unexpected;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(
                targetMethod ?? throw new InvalidOperationException("A proxy method was not supplied."),
                args ?? []);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? Unexpected(MethodInfo method, object?[]? arguments) =>
        throw new InvalidOperationException(
            $"Unexpected {method.DeclaringType?.Name}.{method.Name} call.");
}
