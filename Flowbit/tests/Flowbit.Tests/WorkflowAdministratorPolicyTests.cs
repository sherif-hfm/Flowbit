using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAdministratorPolicyTests
{
    [Theory]
    [InlineData(null, "admin", true)]
    [InlineData("", " ADMIN ", true)]
    [InlineData("  ", "admin", true)]
    [InlineData(" , , ", "admin", true)]
    [InlineData(null, "User", false)]
    [InlineData(" Operators , Process Managers ", " operators ", true)]
    [InlineData(" Operators , Process Managers ", "PROCESS MANAGERS", true)]
    [InlineData("Operators", "admin", false)]
    [InlineData("Operators", "OperatorsAssistant", false)]
    public void RequiredRoleUsesExactCaseInsensitiveTrimmedMatches(
        string? setting,
        string role,
        bool expected) =>
        Assert.Equal(expected, WorkflowAdministratorPolicy.IsAuthorized([role], setting));

    [Fact]
    public async Task RequireRejectsAnAnonymousActorBeforeReadingSettings()
    {
        var settings = new MutableSettings(null);
        foreach (var user in new string?[] { null, "", "  " })
        {
            await Assert.ThrowsAsync<WorkflowUnauthorizedException>(() =>
                WorkflowAdministratorPolicy.RequireAsync(Actor(user, "admin"), settings, CancellationToken.None));
        }
        Assert.Equal(0, settings.Reads);
    }

    [Fact]
    public async Task RequireReadsCurrentSettingOnEveryEntryAndCustomRoleReplacesAdmin()
    {
        var settings = new MutableSettings(null);
        var admin = Actor("operator", "admin");
        await WorkflowAdministratorPolicy.RequireAsync(admin, settings, CancellationToken.None);
        settings.Value = " Process Managers, Operations ";
        await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            WorkflowAdministratorPolicy.RequireAsync(admin, settings, CancellationToken.None));
        await WorkflowAdministratorPolicy.RequireAsync(
            Actor("operator", "operations"), settings, CancellationToken.None);
        settings.Value = " , ";
        await WorkflowAdministratorPolicy.RequireAsync(admin, settings, CancellationToken.None);
        Assert.Equal(4, settings.Reads);
    }

    [Fact]
    public async Task EveryBatchServiceEntryRejectsUnauthorizedCallersBeforeAnyDataOrRetryLookup()
    {
        var settings = new MutableSettings("Operations");
        var service = new AdministrativeActionBatchService(
            null!, null!, null!, null!, settings, null!, new WorkflowContextOptions(), TimeProvider.System);
        foreach (var actor in new[] { ActorContext.Anonymous, Actor("admin-user", "admin"), Actor("plain-user") })
        {
            var calls = new Func<Task>[]
            {
                () => service.ListWorkflowCatalogAsync(actor, CancellationToken.None),
                () => service.ListSourceNodesAsync(1, actor, CancellationToken.None),
                () => service.ListActionsAsync(1, 2, actor, CancellationToken.None),
                () => service.SearchCandidatesAsync(new(), actor, CancellationToken.None),
                () => service.CreateAsync(null!, actor, CancellationToken.None),
                () => service.ListAsync(new(), actor, CancellationToken.None),
                () => service.GetAsync(1, actor, CancellationToken.None),
                () => service.ListItemsAsync(1, null, 1, 20, actor, CancellationToken.None),
                () => service.ConfirmAsync(1, new(0, 0, DateTimeOffset.UtcNow), actor, CancellationToken.None),
                () => service.CancelAsync(1, new(null), actor, CancellationToken.None)
            };
            foreach (var call in calls)
            {
                if (actor == ActorContext.Anonymous)
                {
                    await Assert.ThrowsAsync<WorkflowUnauthorizedException>(call);
                }
                else
                {
                    await Assert.ThrowsAsync<WorkflowForbiddenException>(call);
                }
            }
        }
        Assert.Equal(20, settings.Reads);
    }

    private static ActorContext Actor(string? user, params string[] roles) =>
        new(user, roles, new Dictionary<string, string>());

    private sealed class MutableSettings(string? value) : IEngineSettingsRepository
    {
        public string? Value { get; set; } = value;
        public int Reads { get; private set; }

        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken)
        {
            Assert.Equal(WorkflowAdministratorPolicy.RequiredRoleSettingKey, key);
            Reads++;
            return Task.FromResult(Value is null ? null : new EngineSettingRecord(
                1, "Workflow", "RequiredRole", Value, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        }

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
