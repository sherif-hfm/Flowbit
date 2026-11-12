using System.Text.Json;
using Flowbit.Infrastructure.Data;
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
public sealed partial class AutomaticMultiInstanceCompletionTests(PostgresApiFixture fixture)
{
    [Theory]
    [InlineData(1, 8, "parallel", null, "6", "3", 3)]
    [InlineData(10, 2, "parallel", null, "6", "3", 2)]
    [InlineData(1, 8, "sequential", null, "6", "3", 1)]
    [InlineData(4, 0, "parallel", null, "6", "3", 0)]
    [InlineData(1, 8, "parallel", 2, "6", "3", 2)]
    [InlineData(1, 8, "parallel", null, "invalid", "0", 4)]
    public async Task Initialization_resolves_concurrency_from_current_settings_and_the_newly_captured_count(
        int originalCount, int capturedCount, string mode, int? requestedMaximum,
        string configuredDefault, string configuredMaximum, int expectedConcurrency)
    {
        var settings = new ConcurrencySettings { Default = "1", Maximum = "1" };
        await using var state = await CreateAsync(originalCount, mode, requestedMaximum, settings);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        await state.Runtime.AddVariableAsync(state.Template.InstanceId!.Value, "items", null, "changed-before-entry",
            JsonSerializer.SerializeToElement(Enumerable.Range(0, capturedCount).ToArray()), default);
        await state.Db.SaveChangesAsync();
        settings.Default = configuredDefault;
        settings.Maximum = configuredMaximum;
        var body = await state.InitializeAsync();
        Assert.Equal(capturedCount, body.TotalCount);
        Assert.Equal(expectedConcurrency, body.EffectiveConcurrency);
        Assert.Equal(mode, body.Mode);
        settings.Default = "1000";
        settings.Maximum = "1000";
        Assert.Equal(expectedConcurrency, (await state.Repository.GetAsync(body.Id, false, default))!.EffectiveConcurrency);
        await state.CommitAsync();
    }

    [Theory]
    [InlineData(InstanceHistoryNotes.AutomaticMultiInstanceStarted)]
    [InlineData(InstanceHistoryNotes.AutomaticMultiInstanceFailed)]
    [InlineData(InstanceHistoryNotes.AutomaticMultiInstanceCompleted)]
    public void Automatic_body_traversals_are_never_prior_human_actors(string note)
    {
        var history = new InstanceHistoryRecord(1, 2, 3, null, 4, null, 5, 6, 7,
            "causal-user", null, note, DateTimeOffset.UtcNow);
        Assert.False(WorkflowEngineService.IsActorActionHistory(history));
        Assert.True(WorkflowEngineService.IsActorActionHistory(history with { Note = "userAction" }));
    }

    [Fact]
    public async Task Empty_body_resets_once_then_completes_without_item_or_parent_visits()
    {
        await using var state = await CreateAsync(0);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        var body = await state.InitializeAsync();
        Assert.Equal(new[] { "timers", "drain" }, state.Lifecycle.Events);
        Assert.Empty(await state.Db.NodeExecutions.Where(row => row.InstanceId == body.InstanceId).ToArrayAsync());
        Assert.Empty(await state.Db.UserTasks.Where(row => row.InstanceId == body.InstanceId).ToArrayAsync());
        var fence = await state.LeaseControlAsync(body);
        Assert.NotNull(await state.Coordinator.CompleteBodyAsync(body.Id, body.ActivationId, fence,
            state.Definition, state.Actor, state.Lifecycle, default));
        await state.Db.SaveChangesAsync();
        Assert.Equal(new[] { "timers", "drain", "drain", "close", "continue" }, state.Lifecycle.Events);
        Assert.Equal("completed", (await state.Repository.GetAsync(body.Id, false, default))!.Status);
        Assert.Equal(2, await state.Db.InstanceVariables.CountAsync(row => row.InstanceId == body.InstanceId && row.VariableName == "results"));
        Assert.Equal(2, await state.Db.InstanceHistory.CountAsync(row => row.InstanceId == body.InstanceId));
        Assert.All(await state.Db.InstanceVariables.Where(row => row.InstanceId == body.InstanceId && row.VariableName == "results").ToArrayAsync(),
            row => Assert.Equal("[]", row.ValueJson.RootElement.GetRawText()));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => state.Coordinator.CompleteBodyAsync(body.Id,
            body.ActivationId, fence, state.Definition, state.Actor, state.Lifecycle, default));
        await state.CommitAsync();
    }

    [Fact]
    public async Task Reset_triggered_interruption_keeps_the_write_and_schedules_no_item_work()
    {
        await using var state = await CreateAsync(2);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        state.Lifecycle.OnDrain = async body =>
        {
            await state.Repository.CancelUnfinishedItemsAsync(body.Id, body.ActivationId, "boundaryCaught", default);
            await state.Repository.SettleAsync(body.Id, body.ActivationId, "cancelled", "boundaryCaught", default);
        };
        var body = await state.InitializeAsync();
        Assert.Equal("cancelled", body.Status);
        Assert.Equal("[]", (await state.Runtime.LoadLatestVariableVersionsAsync(body.InstanceId, default)).Single(value => value.Name == "results").Value.GetRawText());
        Assert.Empty(await state.Db.WorkflowJobs.Where(job => job.MultiInstanceExecutionId == body.Id).ToArrayAsync());
        Assert.Equal(2, await state.Db.MultiInstanceActivityItems.CountAsync(item => item.MultiInstanceExecutionId == body.Id && item.Status == "cancelled"));
        Assert.DoesNotContain("continue", state.Lifecycle.Events);
        await state.CommitAsync();
    }

    [Fact]
    public async Task Aggregate_conflict_recovers_saved_results_without_repeating_items()
    {
        await using var state = await CreateAsync(1);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        var body = await state.InitializeAsync();
        var item = Assert.Single(await state.Repository.AdmitAsync(body.Id, body.ActivationId, 1, default));
        await state.AcceptLocalResultAsync(body, item.Id, Json("{\"answer\":42}"));
        var fence = await state.LeaseControlAsync(body);
        await state.Runtime.AddVariableAsync(body.InstanceId, "results", null, "operator", Json("[\"changed\"]"), default);
        await state.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<AutomaticMultiInstancePublicationConflictException>(() => state.Coordinator.CompleteBodyAsync(
            body.Id, body.ActivationId, fence, state.Definition, state.Actor, state.Lifecycle, default));
        Assert.Equal("active", (await state.Repository.GetAsync(body.Id, false, default))!.Status);
        Assert.DoesNotContain("continue", state.Lifecycle.Events);
        await state.Db.WorkflowJobs.Where(job => job.Id == fence.JobId).ExecuteUpdateAsync(update =>
            update.SetProperty(job => job.PublicationFenceRefreshRequested, true));
        var completion = await state.Coordinator.CompleteBodyAsync(body.Id, body.ActivationId, fence,
            state.Definition, state.Actor, state.Lifecycle, default);
        Assert.True(completion!.AggregatePublished);
        var aggregate = (await state.Runtime.LoadLatestVariableVersionsAsync(body.InstanceId, default)).Single(value => value.Name == "results").Value;
        Assert.Equal(42, aggregate[0].GetProperty("variables").GetProperty("answer").GetInt32());
        Assert.Equal("completed", (await state.Repository.GetItemAsync(body.Id, item.Id, false, default))!.Status);
        Assert.Single(state.Lifecycle.Events, value => value == "continue");
        await state.CommitAsync();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Caught_failure_stops_siblings_before_partial_publication_and_preserves_completed_output(int boundaryId)
    {
        await using var state = await CreateAsync(3);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        var body = await state.InitializeAsync();
        var items = await state.Repository.AdmitAsync(body.Id, body.ActivationId, 3, default);
        await state.AcceptLocalResultAsync(body, items[0].Id, Json("{\"answer\":42}"));
        var failedJob = await state.Jobs.EnqueueAsync(state.Template with
        {
            Kind = WorkflowJobKinds.MultiInstanceItem, Phase = WorkflowJobKinds.MultiInstanceItem,
            MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = items[1].Id
        }, default);
        var failureFence = await state.LeaseAsync(failedJob.Id);
        var intent = new AutomaticMultiInstanceCompletionIntent(boundaryId, "remote_failure", items[1].Id);
        Assert.True(await state.Coordinator.CaptureItemFailureAsync(body.Id, items[1].Id, body.ActivationId,
            failureFence, intent, state.Actor, state.Template, state.Lifecycle, default));
        Assert.Equal(new[] { "completed", "failed", "cancelled" },
            (await state.Repository.GetItemsAsync(body.Id, 0, 10, true, default)).Items.Select(item => item.Status));
        Assert.Equal("[]", (await state.Runtime.LoadLatestVariableVersionsAsync(body.InstanceId, default)).Single(value => value.Name == "results").Value.GetRawText());
        var completionFence = await state.LeaseControlAsync(body);
        var result = await state.Coordinator.CompleteBodyAsync(body.Id, body.ActivationId, completionFence,
            state.Definition, state.Actor, state.Lifecycle, default);
        Assert.Equal(intent, result!.ErrorRoute);
        var aggregate = (await state.Runtime.LoadLatestVariableVersionsAsync(body.InstanceId, default)).Single(value => value.Name == "results").Value;
        Assert.Equal(42, aggregate[0].GetProperty("variables").GetProperty("answer").GetInt32());
        Assert.Equal("{}", aggregate[1].GetProperty("variables").GetRawText());
        Assert.Equal("{}", aggregate[2].GetProperty("variables").GetRawText());
        Assert.DoesNotContain("remote_failure", aggregate.GetRawText());
        await state.CommitAsync();
    }

    [Fact]
    public async Task Initialization_captures_current_source_and_duplicate_entry_cannot_reset_it()
    {
        await using var state = await CreateAsync(1);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        await state.Runtime.AddVariableAsync(state.Template.InstanceId!.Value, "items", null, "changed-before-entry",
            Json("[9,9]"), default);
        await state.Db.SaveChangesAsync();
        var body = await state.InitializeAsync();
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(2, body.EffectiveConcurrency);
        Assert.Equal(new[] { 9, 9 }, (await state.Repository.GetInputSnapshotAsync(body.Id, default))!.Value
            .GetProperty("collection").EnumerateArray().Select(item => item.GetInt32()));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => state.InitializeAsync());
        Assert.Single(state.Lifecycle.Events, item => item == "timers");
        Assert.Equal(1, await state.Db.InstanceVariables.CountAsync(row => row.InstanceId == body.InstanceId && row.VariableName == "results"));
        Assert.Equal(1, await state.Db.WorkflowJobs.CountAsync(job => job.MultiInstanceExecutionId == body.Id));
        await state.CommitAsync();
    }

    [Fact]
    public async Task Continuation_failure_rolls_back_publication_and_retries_saved_results()
    {
        await using var state = await CreateAsync(1);
        await using var transaction = await state.Db.Database.BeginTransactionAsync();
        var body = await state.InitializeAsync();
        var item = Assert.Single(await state.Repository.AdmitAsync(body.Id, body.ActivationId, 1, default));
        await state.AcceptLocalResultAsync(body, item.Id, Json("{\"answer\":42}"));
        var fence = await state.LeaseControlAsync(body);
        state.Lifecycle.OnContinue = _ => throw new InvalidOperationException("continuation unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.Coordinator.CompleteBodyAsync(body.Id,
            body.ActivationId, fence, state.Definition, state.Actor, state.Lifecycle, default));
        await state.RollbackAsync();
        Assert.Equal("active", (await state.Repository.GetAsync(body.Id, false, default))!.Status);
        Assert.Equal("[]", (await state.Runtime.LoadLatestVariableVersionsAsync(body.InstanceId, default))
            .Single(value => value.Name == "results").Value.GetRawText());
        Assert.Equal("completed", (await state.Repository.GetItemAsync(body.Id, item.Id, false, default))!.Status);
        Assert.Empty(await state.Db.InstanceHistory.Where(row => row.InstanceId == body.InstanceId
            && row.Note == InstanceHistoryNotes.AutomaticMultiInstanceCompleted).ToArrayAsync());
        await state.Db.Database.BeginTransactionAsync();
        state.Lifecycle.OnContinue = null;
        Assert.True((await state.Coordinator.CompleteBodyAsync(body.Id, body.ActivationId, fence,
            state.Definition, state.Actor, state.Lifecycle, default))!.AggregatePublished);
        await state.CommitAsync();
        Assert.Equal(2, await state.Db.InstanceVariables.CountAsync(row => row.InstanceId == body.InstanceId && row.VariableName == "results"));
        var settlement = await state.Db.InstanceHistory.SingleAsync(row => row.InstanceId == body.InstanceId
            && row.Note == InstanceHistoryNotes.AutomaticMultiInstanceCompleted);
        Assert.Equal("settlement", settlement.Payload!.RootElement.GetProperty("purpose").GetString());
    }

    private async Task<State> CreateAsync(int count, string mode = "parallel", int? requestedMaximum = null,
        IEngineSettingsRepository? engineSettings = null, bool flowInfo = false)
    {
        var db = fixture.CreateDbContext();
        var activityKind = flowInfo ? "scriptTask" : "serviceTask";
        var definition = new WorkflowModel
        {
            Id = "aggregate-core-" + Guid.NewGuid().ToString("N"),
            Variables = [new() { Name = "items", DataType = "number", IsArray = true }, new() { Name = "results", DataType = "json" }],
            FlowNodes = [new() { Id = 2, Name = "Body", Type = activityKind, AsyncBefore = true,
                ScriptFormat = flowInfo ? "javascript" : null, UsesFlowInfo = flowInfo,
                MultiInstance = new() { Mode = mode,
                    CollectionVariable = "items", ResultVariable = "results", CompletionEvaluation = "afterAll", OutputVariables = [] } }]
        };
        if (requestedMaximum is not null) definition.FlowNodes[0].MultiInstance!.MaxConcurrency = requestedMaximum;
        var storedDefinition = new WorkflowDefinitionEntity { WorkflowKey = definition.Id, Name = definition.Id, Version = 1, Definition = definition };
        db.WorkflowDefinitions.Add(storedDefinition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity { WorkflowDefinitionId = storedDefinition.Id, WorkflowKey = definition.Id, Status = "running" };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        var token = new ExecutionTokenEntity { InstanceId = instance.Id, NodeId = 2, NodeName = "Body", NodeType = activityKind };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        var template = new WorkflowJobCreateRecord
        {
            InstanceId = instance.Id, TokenId = token.Id, WorkflowDefinitionId = storedDefinition.Id, WorkflowKey = definition.Id,
            ActivationId = token.ActivationId, NodeId = 2, NodeName = "Body", NodeType = activityKind,
            Kind = "asyncBefore", Phase = "asyncBefore", QueueClass = "activity", DueAt = DateTimeOffset.UtcNow, MaxAttempts = 4
        };
        var entry = await jobs.EnqueueAsync(template, default);
        token.WaitState = "asyncBefore"; token.WaitingJobId = entry.Id;
        await db.SaveChangesAsync();
        var repository = new AutomaticMultiInstanceRepository(db);
        var runtime = new WorkflowRuntimeRepository(db);
        await runtime.AddVariableAsync(instance.Id, "items", null, "test",
            JsonSerializer.SerializeToElement(Enumerable.Range(0, count).ToArray()), default);
        await db.SaveChangesAsync();
        var coordinator = new AutomaticMultiInstanceCoordinator(repository, jobs,
            new WorkflowVariableStore(runtime, new SharedVariableRepository(db)),
            fixture.Factory.Services.GetRequiredService<ISharedVariableAccessPlanCache>(), runtime, new UnitOfWork(db), engineSettings);
        var create = new AutomaticMultiInstanceCreateRecord
        {
            InstanceId = instance.Id, TokenId = token.Id, WorkflowDefinitionId = storedDefinition.Id,
            ActivationId = token.ActivationId, NodeId = 2, NodeName = "Body", ActivityKind = activityKind,
            Mode = mode, ResultVariable = "results", AggregateScope = "instance", AggregateValueVersion = 0,
            MaxInstances = 1000, EffectiveConcurrency = count, TotalCount = count, ResultBytes = 2,
            InputSnapshot = JsonSerializer.SerializeToElement(new { collection = Enumerable.Range(0, count).ToArray() })
        };
        var state = new State(db, repository, runtime, jobs, coordinator, definition, create, template);
        state.EntryFence = await state.LeaseAsync(entry.Id);
        return state;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class ConcurrencySettings : IEngineSettingsRepository
    {
        public string? Default { get; set; }
        public string? Maximum { get; set; }
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken ct)
        {
            var value = key == AutomaticMultiInstanceLimits.DefaultConcurrencyKey ? Default : Maximum;
            return Task.FromResult(value is null ? null : new EngineSettingRecord(1, "Workflow.MultiInstance", key,
                value, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        }
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, string actor, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class State(AppDbContext db, AutomaticMultiInstanceRepository repository, WorkflowRuntimeRepository runtime,
        WorkflowJobRepository jobs, AutomaticMultiInstanceCoordinator coordinator, WorkflowModel definition,
        AutomaticMultiInstanceCreateRecord create, WorkflowJobCreateRecord template) : IAsyncDisposable
    {
        public AppDbContext Db => db;
        public AutomaticMultiInstanceRepository Repository => repository;
        public WorkflowRuntimeRepository Runtime => runtime;
        public WorkflowJobRepository Jobs => jobs;
        public AutomaticMultiInstanceCoordinator Coordinator => coordinator;
        public WorkflowModel Definition => definition;
        public AutomaticMultiInstanceCreateRecord Create => create;
        public WorkflowJobCreateRecord Template => template;
        public WorkflowJobFence EntryFence = null!;
        public ActorContext Actor { get; } = new("causal", [], new Dictionary<string, string>());
        public Lifecycle Lifecycle { get; } = new();
        public async Task<AutomaticMultiInstanceRecord> InitializeAsync() =>
            (await coordinator.InitializeBodyAsync(create, definition, Actor, EntryFence, template, Lifecycle, default))!;
        public async Task AcceptLocalResultAsync(AutomaticMultiInstanceRecord body, long itemId, JsonElement result)
        {
            Assert.True(await repository.SaveStageAsync(body.Id, itemId, body.ActivationId, Json("{}"), default));
            Assert.True(await repository.SaveCandidateAsync(body.Id, itemId, body.ActivationId, result, default));
            Assert.True(await repository.CompleteItemAsync(body.Id, itemId, body.ActivationId, result, null,
                JsonSerializer.SerializeToUtf8Bytes(result).Length - 2, default));
        }
        public async Task<WorkflowJobFence> LeaseControlAsync(AutomaticMultiInstanceRecord body)
        {
            var job = await db.WorkflowJobs.SingleAsync(job => job.MultiInstanceExecutionId == body.Id
                && (job.Kind == "multiInstanceDispatch" || job.Kind == "multiInstanceComplete") && job.Status == "queued");
            job.Kind = "multiInstanceComplete"; job.Phase = "multiInstanceComplete";
            await db.SaveChangesAsync();
            return await LeaseAsync(job.Id);
        }
        public async Task<WorkflowJobFence> LeaseAsync(long id)
        {
            var reopen = db.Database.CurrentTransaction is not null;
            if (reopen) await CommitAsync();
            var lease = Guid.NewGuid();
            await db.WorkflowJobs.Where(job => job.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "running").SetProperty(job => job.WorkerId, "core-test")
                .SetProperty(job => job.LeaseToken, lease).SetProperty(job => job.LeaseGeneration, 1)
                .SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(10)));
            db.ChangeTracker.Clear();
            if (reopen) await db.Database.BeginTransactionAsync();
            return new(id, "core-test", lease, 1);
        }
        public async Task CommitAsync()
        {
            await db.SaveChangesAsync();
            if (db.Database.CurrentTransaction is { } transaction)
            {
                await transaction.CommitAsync();
                await transaction.DisposeAsync();
            }
        }
        public async Task RollbackAsync()
        {
            if (db.Database.CurrentTransaction is { } transaction)
            {
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
            }
            db.ChangeTracker.Clear();
        }
        public async ValueTask DisposeAsync()
        {
            await RollbackAsync();
            // Several scenarios deliberately leave the body open. Retire its
            // durable wake so unrelated tests cannot acquire this control job.
            await jobs.CancelByInstanceAsync(create.InstanceId, "testCleanup", default);
            await db.DisposeAsync();
        }
    }

    private sealed class Lifecycle : IAutomaticMultiInstanceLifecycle
    {
        public List<string> Events { get; } = [];
        public Func<AutomaticMultiInstanceRecord, Task>? OnDrain;
        public Func<AutomaticMultiInstanceRecord, Task>? OnContinue;
        public Task InstallHostTimersAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) { Events.Add("timers"); return Task.CompletedTask; }
        public async Task DrainVariableNotificationsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct)
        { Events.Add("drain"); if (OnDrain is not null) await OnDrain(body); }
        public Task CloseHostSubscriptionsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) { Events.Add("close"); return Task.CompletedTask; }
        public Task AbandonItemCallsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) { Events.Add("abandon"); return Task.CompletedTask; }
        public async Task ContinueAsync(AutomaticMultiInstanceRecord body, AutomaticMultiInstanceCompletion completion, CancellationToken ct)
        { Events.Add("continue"); if (OnContinue is not null) await OnContinue(body); }
    }
}
