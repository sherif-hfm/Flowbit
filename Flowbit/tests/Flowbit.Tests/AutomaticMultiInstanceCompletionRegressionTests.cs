using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class AutomaticMultiInstanceCompletionTests
{
    [Fact]
    public async Task Published_shared_write_is_visible_on_the_item_node_execution_once()
    {
        await using var state = await CreateAsync(1);
        var key = "mi-audit-" + Guid.NewGuid().ToString("N");
        var shared = new SharedVariableRepository(state.Db);
        await shared.CreateAsync(new(key, "number", false, false, null, null, true, Json("0"),
            new("user", "test", [], []), "system", null, null, null), default);
        state.Definition.Variables.Add(new() { Name = "sharedOutput", Scope = "shared", SharedKey = key,
            Access = "readWrite", DataType = "number" });
        state.Definition.FlowNodes[0].Service = new() { OutputMappings = [new() { Variable = "sharedOutput", DataType = "number" }] };
        await using var tx = await state.Db.Database.BeginTransactionAsync();
        var body = await state.InitializeAsync();
        var item = Assert.Single(await state.Repository.AdmitAsync(body.Id, body.ActivationId, 1, default));
        var job = await state.Jobs.EnqueueAsync(state.Template with { Kind = "multiInstanceItem", Phase = "multiInstanceItem",
            MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = item.Id }, default);
        var fence = await state.LeaseAsync(job.Id);
        var context = await state.Coordinator.CaptureItemContextAsync(body.Id, item.Id, body.ActivationId, fence,
            state.Definition, new Dictionary<string, JsonElement>(), DateTimeOffset.UtcNow, default);
        await state.Coordinator.SaveCandidateAsync(body.Id, item.Id, body.ActivationId, fence, state.Definition,
            new(new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement> { ["sharedOutput"] = Json("7") },
                context.SharedOutputValueVersions, context.Values), default);
        // Candidate storage must commit independently of its publication.
        await state.CommitAsync();
        await state.Db.Database.BeginTransactionAsync();
        Assert.True(await state.Coordinator.PublishCandidateAsync(body.Id, item.Id, body.ActivationId, fence,
            state.Definition, state.Actor, state.Template, default));
        await state.CommitAsync();
        var visitId = (await state.Repository.GetItemAsync(body.Id, item.Id, false, default))!.NodeExecutionId!.Value;
        var detail = await new NodeExecutionQueryRepository(state.Db).GetAsync(visitId, new(true, []), default);
        Assert.Equal("causal", detail!.StartedBy);
        Assert.Equal("causal", detail.CompletedBy);
        var write = Assert.Single(detail!.SharedVariableChanges);
        Assert.Equal(key, write.Key);
        await state.Db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => state.Coordinator.PublishCandidateAsync(body.Id, item.Id,
            body.ActivationId, fence, state.Definition, state.Actor, state.Template, default));
        Assert.Single((await new NodeExecutionQueryRepository(state.Db).GetAsync(visitId, new(true, []), default))!.SharedVariableChanges);
    }

    [Fact]
    public async Task Successful_initialization_captures_current_flow_info_once_for_all_attempts()
    {
        await using var state = await CreateAsync(1, flowInfo: true);
        state.Definition.SequenceFlows.Add(new() { Id = 103, SourceRef = 1, TargetRef = 2 });
        state.Db.SequenceFlowSummaries.Add(new() { InstanceId = state.Create.InstanceId, SequenceFlowId = 103, TraversalCount = 7 });
        await state.Db.SaveChangesAsync();
        await using var tx = await state.Db.Database.BeginTransactionAsync();
        var stale = AutomaticMultiInstanceValues.CreateInputSnapshot([Json("99")], new Dictionary<string, JsonElement>(),
            DateTimeOffset.UnixEpoch, flowInfo: new([103], [SequenceFlowRuntimeSummary.Empty(103)]));
        var body = (await state.Coordinator.InitializeBodyAsync(state.Create with { InputSnapshot = stale }, state.Definition,
            state.Actor, state.EntryFence, state.Template, state.Lifecycle, default))!;
        var item = Assert.Single(await state.Repository.AdmitAsync(body.Id, body.ActivationId, 1, default));
        var job = await state.Jobs.EnqueueAsync(state.Template with { Kind = "multiInstanceItem", Phase = "multiInstanceItem",
            MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = item.Id }, default);
        var fence = await state.LeaseAsync(job.Id);
        var first = await state.Coordinator.CaptureItemContextAsync(body.Id, item.Id, body.ActivationId, fence, state.Definition,
            new Dictionary<string, JsonElement>(), DateTimeOffset.UtcNow, default);
        await state.Db.SequenceFlowSummaries.Where(row => row.InstanceId == body.InstanceId).ExecuteUpdateAsync(update =>
            update.SetProperty(row => row.TraversalCount, 9));
        var retry = await state.Coordinator.CaptureItemContextAsync(body.Id, item.Id, body.ActivationId, fence, state.Definition,
            new Dictionary<string, JsonElement>(), DateTimeOffset.UtcNow, default);
        Assert.Equal(7, first.FlowInfo!.GetSummary(103).Traversals.Count);
        Assert.Equal(7, retry.FlowInfo!.GetSummary(103).Traversals.Count);
        Assert.Equal(0, retry.Values["mi.item"].GetInt32());
        var storedItem = await state.Repository.GetItemAsync(body.Id, item.Id, false, default);
        Assert.DoesNotContain("flowInfo", storedItem!.StagedInput!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var snapshot = await state.Db.WorkflowJobSnapshots.AsNoTracking().SingleAsync(row => row.Id == body.InputSnapshotId);
        Assert.InRange(snapshot.SizeBytes, JsonSerializer.SerializeToUtf8Bytes(first.FlowInfo).Length,
            Flowbit.Shared.Models.AutomaticMultiInstanceRules.MaxSnapshotBytes);
    }

    [Fact]
    public async Task Non_output_completion_retry_preserves_fence_until_explicit_output_conflict_acceptance()
    {
        await using var state = await CreateAsync(1);
        await using var tx = await state.Db.Database.BeginTransactionAsync();
        var body = await state.InitializeAsync();
        var item = Assert.Single(await state.Repository.AdmitAsync(body.Id, body.ActivationId, 1, default));
        await state.AcceptLocalResultAsync(body, item.Id, Json("{\"answer\":42}"));
        var fence = await state.LeaseControlAsync(body);
        var incident = await state.Jobs.OpenIncidentAsync(fence, "job_conflict", "Catalog temporarily unavailable", null, default);
        await state.Runtime.AddVariableAsync(body.InstanceId, "results", null, "operator", Json("[99]"), default);
        await state.CommitAsync();
        var retry = await state.Jobs.RetryIncidentAsync(incident!.Id, "operator", DateTimeOffset.UtcNow, default);
        Assert.False(retry!.PublicationFenceRefreshRequested);
        fence = await state.LeaseAsync(retry.Id);
        await state.Db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<AutomaticMultiInstancePublicationConflictException>(() => state.Coordinator.CompleteBodyAsync(
            body.Id, body.ActivationId, fence, state.Definition, state.Actor, state.Lifecycle, default));
        incident = await state.Jobs.OpenIncidentAsync(fence, "output_version_conflict", "Version changed", null, default);
        await state.CommitAsync();
        retry = await state.Jobs.RetryIncidentAsync(incident!.Id, "operator", DateTimeOffset.UtcNow, default);
        Assert.True(retry!.PublicationFenceRefreshRequested);
        fence = await state.LeaseAsync(retry.Id);
        await state.Db.Database.BeginTransactionAsync();
        var completion = await state.Coordinator.CompleteBodyAsync(body.Id, body.ActivationId, fence,
            state.Definition, state.Actor, state.Lifecycle, default);
        Assert.True(completion!.AggregatePublished);
        Assert.Single(state.Lifecycle.Events, e => e == "continue");
    }
}
