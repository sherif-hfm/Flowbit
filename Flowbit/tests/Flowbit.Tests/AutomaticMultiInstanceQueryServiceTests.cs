using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceQueryServiceTests
{
    [Theory]
    [InlineData(null, "ADMIN", true)]
    [InlineData(" ", "admin", true)]
    [InlineData("auditor, security", "AUDITOR", true)]
    [InlineData("auditor", "admin", false)]
    [InlineData("auditor", "Team-Manager", false)]
    public async Task Detail_uses_node_activity_scope_without_granting_instance_readers_payload_access(
        string? setting, string role, bool global)
    {
        var repository = new QueryRepository();
        var service = new AutomaticMultiInstanceQueryService(repository, new Settings(setting));
        await service.GetItemAsync(17, 29, Actor(role), CancellationToken.None);
        Assert.Equal(17, repository.ExecutionId);
        Assert.Equal(29, repository.ItemId);
        Assert.Equal(global, repository.Authorization!.IsGlobalReader);
        Assert.Equal(role.ToLowerInvariant(), Assert.Single(repository.Authorization.LowerCallerRoles));
    }

    [Fact]
    public async Task Summaries_use_existing_instance_visibility_and_bounded_pagination()
    {
        var repository = new QueryRepository();
        var service = new AutomaticMultiInstanceQueryService(repository, new Settings("admin"));
        var result = await service.ListItemsAsync(17, 0, 500, Actor("reader"), CancellationToken.None);
        Assert.Equal(1, result!.Page);
        Assert.Equal(200, result.PageSize);
        Assert.False(repository.Authorization!.IsGlobalReader);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(-1, 3)]
    public async Task Invalid_identifiers_fail_before_repository_access(long executionId, long itemId)
    {
        var repository = new QueryRepository();
        var service = new AutomaticMultiInstanceQueryService(repository, new Settings("admin"));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.GetItemAsync(executionId, itemId, Actor("reader"), CancellationToken.None));
        Assert.Null(repository.Authorization);
    }

    private static ActorContext Actor(params string[] roles) => new("reader", roles, new Dictionary<string, string>());

    private sealed class QueryRepository : IAutomaticMultiInstanceQueryRepository
    {
        public NodeExecutionAuthorization? Authorization { get; private set; }
        public long ExecutionId { get; private set; }
        public long ItemId { get; private set; }
        public Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>?> ListItemsAsync(long executionId, int page, int pageSize, NodeExecutionAuthorization authorization, CancellationToken cancellationToken)
        {
            ExecutionId = executionId;
            Authorization = authorization;
            return Task.FromResult<PagedResult<AutomaticMultiInstanceItemSummaryDto>?>(new([], page, pageSize, 0));
        }
        public Task<AutomaticMultiInstanceItemDetailDto?> GetItemAsync(long executionId, long itemId, NodeExecutionAuthorization authorization, CancellationToken cancellationToken)
        {
            ExecutionId = executionId; ItemId = itemId; Authorization = authorization;
            return Task.FromResult<AutomaticMultiInstanceItemDetailDto?>(null);
        }
    }

    private sealed class Settings(string? value) : IEngineSettingsRepository
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(value is null ? null : new EngineSettingRecord(1, "NodeExecution", "RequiredRole", value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EngineSettingRecord>>([]);
        public Task<EngineSettingRecord> SetAsync(string key, string settingValue, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
