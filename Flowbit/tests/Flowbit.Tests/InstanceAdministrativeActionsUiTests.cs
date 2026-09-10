extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using InstancePanel = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceAdministrativeActions;
using BatchPage = FlowbitUi::Flowbit.Ui.Components.Pages.AdministrativeActions;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceAdministrativeActionsUiTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task DeniedDiscoveryHidesAdministrativeActions(HttpStatusCode status)
    {
        await using var harness = new Harness(_ => Task.FromResult(Response(status, new { error = "denied" })));
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters());

        Assert.DoesNotContain("Administrative actions", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Equal("/api/instances/42/administrative-actions?page=1&pageSize=25", Assert.Single(harness.Paths));
    }

    [Fact]
    public async Task ConfiguredRoleUsesApiPermissionAndShowsParallelPositionsAndPaging()
    {
        var first = Position(73, multiInstance: true);
        var second = Position(74, multiInstance: false);
        await using var harness = new Harness(_ => Task.FromResult(Response(HttpStatusCode.OK,
            new PagedResult<InstanceAdministrativeActionPositionDto>([first, second], 1, 25, 30))));
        harness.Token.ApplyResolvedContext(new ActorContextDto("operator", ["custom-operator"]));
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters());
        var html = await harness.HtmlAsync(component);

        Assert.Contains("Administrative actions", html, StringComparison.Ordinal);
        Assert.Contains("Multi-instance execution #73", html, StringComparison.Ordinal);
        Assert.Contains("Task #74", html, StringComparison.Ordinal);
        Assert.Contains("6 task(s) affected", html, StringComparison.Ordinal);
        Assert.Contains("To Finished", html, StringComparison.Ordinal);
        Assert.Contains("Page 1 of 2", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshReturnsToAValidPageWhenRemainingTasksShrink()
    {
        var item = Position(73, multiInstance: false, inputs: false);
        var requests = 0;
        await using var harness = new Harness(_ => Task.FromResult(Response(HttpStatusCode.OK, ++requests switch
        {
            1 => new PagedResult<InstanceAdministrativeActionPositionDto>([item], 1, 25, 26),
            2 => new PagedResult<InstanceAdministrativeActionPositionDto>([item], 2, 25, 26),
            3 => new PagedResult<InstanceAdministrativeActionPositionDto>([], 2, 25, 25),
            _ => new PagedResult<InstanceAdministrativeActionPositionDto>([item], 1, 25, 25)
        })));
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters());
        await harness.CallAsync("ChangePageAsync", 1);
        await harness.CallAsync("RefreshAsync");
        var html = await harness.HtmlAsync(component);

        Assert.Contains("Task #73", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No active human-task positions", html, StringComparison.Ordinal);
        Assert.EndsWith("page=1&pageSize=25", harness.Paths.Last(), StringComparison.Ordinal);
        Assert.Equal(4, requests);
    }

    [Theory]
    [InlineData(AdministrativeActionMultiInstanceModes.ForceParent)]
    [InlineData(AdministrativeActionMultiInstanceModes.CompleteAllChildren)]
    public async Task MultiInstanceRequiresExplicitModeParsesTypedInputsAndSubmitsOnce(string mode)
    {
        var item = Position(73, multiInstance: true);
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonElement posted = default;
        var posts = 0;
        var refreshes = 0;
        await using var harness = new Harness(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                posted = await request.Content!.ReadFromJsonAsync<JsonElement>();
                postStarted.TrySetResult();
                await finishPost.Task;
                return Response(HttpStatusCode.OK, new AdministrativeActionResultDto(CompletedInstance(), item.Position.PositionKind, 73, 6, 98));
            }
            return Response(HttpStatusCode.OK, new PagedResult<InstanceAdministrativeActionPositionDto>([item], 1, 25, 1));
        });
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters(
            EventCallback.Factory.Create(new object(), () => refreshes++)));
        await harness.CallAsync("SelectAction", item.Position, item.Actions[0]);
        await harness.CallAsync("ReviewAction");
        Assert.Contains("Choose a multi-instance operation.", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.SetAsync("multiInstanceMode", mode);
        await harness.CallAsync("ReviewAction");
        Assert.Contains("is required.", await harness.HtmlAsync(component), StringComparison.Ordinal);
        await harness.CallAsync("SetInput", "approved", "true");
        await harness.CallAsync("SetInput", "amounts", "[12, 14.5]");
        await harness.CallAsync("SetInput", "context", "{\"source\":\"repair\"}");
        await harness.SetAsync("reason", "Correct imported state");
        await harness.CallAsync("ReviewAction");
        Assert.Contains("Confirm administrative action", await harness.HtmlAsync(component), StringComparison.Ordinal);

        var execution = harness.CallAsync("ExecuteAsync");
        await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.CallAsync("ExecuteAsync");
        Assert.Equal(1, posts);
        finishPost.TrySetResult();
        await execution;

        Assert.Equal(1, refreshes);
        Assert.Equal(mode, posted.GetProperty("multiInstanceMode").GetString());
        Assert.True(posted.GetProperty("variables").GetProperty("approved").GetBoolean());
        Assert.Equal(14.5m, posted.GetProperty("variables").GetProperty("amounts")[1].GetDecimal());
        Assert.Equal("repair", posted.GetProperty("variables").GetProperty("context").GetProperty("source").GetString());
        Assert.Equal(item.Position.TokenActivationId, posted.GetProperty("expectedTokenActivationId").GetGuid());
        Assert.Equal(6, posted.GetProperty("expectedAffectedTaskCount").GetInt32());
        Assert.False(posted.TryGetProperty("actor", out _));
        Assert.False(posted.TryGetProperty("roles", out _));
        Assert.False(posted.TryGetProperty("batchId", out _));
        Assert.Contains("View audit batch #98", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictClearsSelectionRefreshesAndDoesNotRetry()
    {
        var item = Position(74, multiInstance: false, inputs: false);
        var posts = 0;
        var refreshes = 0;
        await using var harness = new Harness(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                return Task.FromResult(Response(HttpStatusCode.Conflict, new { error = "stale" }));
            }
            return Task.FromResult(Response(HttpStatusCode.OK, new PagedResult<InstanceAdministrativeActionPositionDto>([item], 1, 25, 1)));
        });
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters(EventCallback.Factory.Create(new object(), () => refreshes++)));
        await harness.CallAsync("SelectAction", item.Position, item.Actions[0]);
        await harness.CallAsync("ReviewAction");
        await harness.CallAsync("ExecuteAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Equal(1, posts);
        Assert.Equal(1, refreshes);
        Assert.Contains("select and review an action again", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirm administrative action", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityChangeCannotReintroduceAnOutstandingAdministrativeResponse()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newIdentityChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var harness = new Harness(_ =>
        {
            if (++requests == 1) return pending.Task;
            newIdentityChecked.TrySetResult();
            return Task.FromResult(Response(HttpStatusCode.Forbidden, new { error = "denied" }));
        });
        var render = harness.RenderAsync<InstancePanel>(InstanceParameters());
        await harness.DispatchAsync(() => harness.Token.Set("ordinary-token"));
        await newIdentityChecked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.SetResult(Response(HttpStatusCode.OK,
            new PagedResult<InstanceAdministrativeActionPositionDto>([Position(73, true)], 1, 25, 1)));
        var component = await render;

        Assert.DoesNotContain("Administrative actions", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.DoesNotContain("Approval", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchPageShowsRoleDenialAndDoesNotExposeSelectors()
    {
        await using var harness = new Harness(_ => Task.FromResult(Response(HttpStatusCode.Forbidden, new { error = "denied" })));
        var component = await harness.RenderAsync<BatchPage>(ParameterView.Empty);
        var html = await harness.HtmlAsync(component);

        Assert.Contains("Workflow administrator permission is required", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"admin-workflow-family\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("does not require a workflow role", html, StringComparison.Ordinal);
        Assert.Single(harness.Paths);
    }

    [Fact]
    public async Task BatchPageRendersVersionNumbersInDiscoveryCandidatesAuditAndRecentBatches()
    {
        var now = DateTimeOffset.Parse("2026-09-10T10:00:00Z");
        var item = Position(73, multiInstance: false, inputs: false);
        var summary = new AdministrativeActionBatchSummaryDto(98, "approval", 8, 3, 7, "Approval", AdministrativeActionKinds.DirectFlow,
            14, null, null, null, "completed", "operator", "operator", 1, 1, 1, 0, 0, 1, 0, 0, 0, now, now, now);
        var batch = new AdministrativeActionBatchDetailDto(summary, item.Actions[0], new Dictionary<string, JsonElement>(),
            JsonSerializer.SerializeToElement(new { }), ["admin"], ["admin"], null, null, null, null, null, now, now, now, null);
        await using var harness = new Harness(request => Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath switch
        {
            "/api/administrative-actions/workflows" => new[] { new WorkflowSummaryDto(8, "Approval", "approval", 3, true, true, now) },
            "/api/workflows/8/administrative-actions/nodes" => new[] { new AdministrativeActionSourceNodeDto(8, 3, 7, "Approval", null, false) },
            "/api/workflows/8/nodes/7/administrative-actions" => item.Actions,
            "/api/administrative-actions/candidates/search" => new PagedResult<AdministrativeActionCandidateDto>([item.Position], 1, 25, 1),
            "/api/administrative-action-batches/98" => batch,
            "/api/administrative-action-batches/98/items" => new PagedResult<AdministrativeActionBatchItemDto>([], 1, 25, 0),
            _ => (object)new PagedResult<AdministrativeActionBatchSummaryDto>([summary], 1, 25, 1)
        })));
        var component = await harness.RenderAsync<BatchPage>(ParameterView.Empty);
        await harness.CallBatchAsync("SearchCandidatesAsync");
        await harness.CallBatchAsync("OpenBatchAsync", 98L);
        var html = await harness.HtmlAsync(component);

        Assert.Contains("#8, v3", html, StringComparison.Ordinal);
        Assert.Contains("Node #7", html, StringComparison.Ordinal);
        Assert.Contains("approval · v3", html, StringComparison.Ordinal);
        Assert.Contains("Preparation and execution", html, StringComparison.Ordinal);
        Assert.DoesNotContain("v@", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchIdentityChangeDiscardsOldCatalogAndSelectionData()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newIdentityChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var harness = new Harness(_ =>
        {
            if (++requests == 1) return pending.Task;
            newIdentityChecked.TrySetResult();
            return Task.FromResult(Response(HttpStatusCode.Forbidden, new { error = "denied" }));
        });
        var render = harness.RenderAsync<BatchPage>(ParameterView.Empty);
        await harness.DispatchAsync(() => harness.Token.Set("ordinary-token"));
        await newIdentityChecked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.SetResult(Response(HttpStatusCode.OK, new[]
        {
            new WorkflowSummaryDto(8, "Old identity confidential workflow", "approval", 3, true, true, DateTimeOffset.UtcNow)
        }));
        var component = await render;
        var html = await harness.HtmlAsync(component);

        Assert.Contains("Workflow administrator permission is required", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Old identity confidential workflow", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"admin-workflow-family\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nodes", HttpStatusCode.OK)]
    [InlineData("nodes", HttpStatusCode.Forbidden)]
    [InlineData("actions", HttpStatusCode.OK)]
    [InlineData("actions", HttpStatusCode.InternalServerError)]
    public async Task OldBatchDiscoveryCannotClearCurrentIdentityActionsOrSelection(string delayedStage, HttpStatusCode oldStatus)
    {
        var oldRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentLoadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogs = 0;
        await using var harness = new Harness(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var definitionId = path.Contains("/8/", StringComparison.Ordinal) ? 8L : 9L;
            var stage = path.EndsWith("/administrative-actions/nodes", StringComparison.Ordinal) ? "nodes" : "actions";
            if (definitionId == 8 && stage == delayedStage)
            {
                oldRequestStarted.TrySetResult();
                await releaseOldRequest.Task;
                if (oldStatus != HttpStatusCode.OK) return Response(oldStatus, new { error = "Old identity request failed" });
            }
            if (path == "/api/administrative-actions/workflows")
                return BatchCatalogResponse(++catalogs == 1 ? 8 : 9);
            if (path == "/api/administrative-action-batches")
                currentLoadCompleted.TrySetResult();
            return BatchDiscoveryResponse(path, definitionId);
        });

        var oldRender = harness.RenderAsync<BatchPage>(ParameterView.Empty);
        await oldRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.DispatchAsync(() => harness.Token.Set("current-admin-token"));
        await currentLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.CallBatchAsync("SearchCandidatesAsync");
        await harness.CallBatchAsync("SelectCurrentCandidatePage");
        Assert.True(await harness.BatchValueAsync<bool>("CanCreateBatch"));

        releaseOldRequest.TrySetResult();
        var component = await oldRender;
        var html = await harness.HtmlAsync(component);

        Assert.Contains("Current task", html, StringComparison.Ordinal);
        Assert.Contains("Current approval", html, StringComparison.Ordinal);
        Assert.Contains("User task #74", html, StringComparison.Ordinal);
        Assert.True(await harness.BatchValueAsync<bool>("CanCreateBatch"));
        Assert.DoesNotContain("Old identity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Workflow administrator permission is required", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("catalog", "loading")]
    [InlineData("nodes", "loadingDefinition")]
    [InlineData("actions", "loadingDefinition")]
    [InlineData("batches", "loadingBatches")]
    public async Task OldBatchDiscoveryCannotClearCurrentIdentityLoadingState(string delayedStage, string loadingField)
    {
        var oldRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCurrentRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogs = 0;
        await using var harness = new Harness(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/administrative-actions/workflows") catalogs++;
            var definitionId = catalogs == 1 ? 8L : 9L;
            var stage = path switch
            {
                "/api/administrative-actions/workflows" => "catalog",
                "/api/administrative-action-batches" => "batches",
                _ when path.EndsWith("/administrative-actions/nodes", StringComparison.Ordinal) => "nodes",
                _ => "actions"
            };
            if (stage == delayedStage)
            {
                (definitionId == 8 ? oldRequestStarted : currentRequestStarted).TrySetResult();
                await (definitionId == 8 ? releaseOldRequest : releaseCurrentRequest).Task;
            }
            return path == "/api/administrative-actions/workflows"
                ? BatchCatalogResponse(definitionId)
                : BatchDiscoveryResponse(path, definitionId);
        });

        var oldRender = harness.RenderAsync<BatchPage>(ParameterView.Empty);
        await oldRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.DispatchAsync(() => harness.Token.Set("current-admin-token"));
        await currentRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await harness.BatchValueAsync<bool>(loadingField));

        releaseOldRequest.TrySetResult();
        var component = await oldRender;
        Assert.True(await harness.BatchValueAsync<bool>(loadingField));
        releaseCurrentRequest.TrySetResult();
        await harness.WaitForBatchValueAsync(loadingField, false);
        Assert.Contains("Current approval", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldBatchSearchCannotEnableSearchWhileCurrentIdentitySearchIsPending()
    {
        var oldSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCurrentSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentLoadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogs = 0;
        await using var harness = new Harness(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/administrative-actions/workflows") return BatchCatalogResponse(++catalogs == 1 ? 8 : 9);
            var definitionId = catalogs == 1 ? 8L : 9L;
            if (path == "/api/administrative-actions/candidates/search")
            {
                (definitionId == 8 ? oldSearchStarted : currentSearchStarted).TrySetResult();
                await (definitionId == 8 ? releaseOldSearch : releaseCurrentSearch).Task;
            }
            if (path == "/api/administrative-action-batches" && definitionId == 9) currentLoadCompleted.TrySetResult();
            return BatchDiscoveryResponse(path, definitionId);
        });

        var component = await harness.RenderAsync<BatchPage>(ParameterView.Empty);
        var oldSearch = harness.CallBatchAsync("SearchCandidatesAsync");
        await oldSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.DispatchAsync(() => harness.Token.Set("current-admin-token"));
        await currentLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var currentSearch = harness.CallBatchAsync("SearchCandidatesAsync");
        await currentSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseOldSearch.TrySetResult();
        await oldSearch;
        Assert.True(await harness.BatchValueAsync<bool>("candidateLoading"));
        Assert.False(await harness.BatchValueAsync<bool>("CanSearchCandidates"));
        releaseCurrentSearch.TrySetResult();
        await currentSearch;
        Assert.True(await harness.BatchValueAsync<bool>("CanSearchCandidates"));
        Assert.Contains("User task #74", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermissionLossOnSubmitClearsActionAndAuditInputs()
    {
        var item = Position(74, multiInstance: false, inputs: false);
        await using var harness = new Harness(request => Task.FromResult(request.Method == HttpMethod.Post
            ? Response(HttpStatusCode.Forbidden, new { error = "denied" })
            : Response(HttpStatusCode.OK, new PagedResult<InstanceAdministrativeActionPositionDto>([item], 1, 25, 1))));
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters());
        await harness.CallAsync("SelectAction", item.Position, item.Actions[0]);
        await harness.SetAsync("reason", "Sensitive operation reason");
        await harness.CallAsync("ReviewAction");
        await harness.CallAsync("ExecuteAsync");
        var html = await harness.HtmlAsync(component);

        Assert.DoesNotContain("Sensitive operation reason", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrative actions", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Approval", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedRefreshInvalidatesAnAlreadyPendingSubmissionResult()
    {
        var item = Position(74, multiInstance: false, inputs: false);
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gets = 0;
        var refreshes = 0;
        await using var harness = new Harness(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postStarted.TrySetResult();
                await finishPost.Task;
                return Response(HttpStatusCode.OK, new AdministrativeActionResultDto(CompletedInstance(), item.Position.PositionKind, 74, 1, 98));
            }
            return ++gets == 1
                ? Response(HttpStatusCode.OK, new PagedResult<InstanceAdministrativeActionPositionDto>([item], 1, 25, 1))
                : Response(HttpStatusCode.Forbidden, new { error = "denied" });
        });
        var component = await harness.RenderAsync<InstancePanel>(InstanceParameters(EventCallback.Factory.Create(new object(), () => refreshes++)));
        await harness.CallAsync("SelectAction", item.Position, item.Actions[0]);
        await harness.CallAsync("ReviewAction");
        var execution = harness.CallAsync("ExecuteAsync");
        await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.CallAsync("LoadAsync");
        finishPost.TrySetResult();
        await execution;

        Assert.Equal(0, refreshes);
        Assert.DoesNotContain("Administrative actions", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.DoesNotContain("batch #98", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    private static ParameterView InstanceParameters(EventCallback refresh = default) => ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        [nameof(InstancePanel.InstanceId)] = 42L,
        [nameof(InstancePanel.OnExecuted)] = refresh
    });

    private static InstanceAdministrativeActionPositionDto Position(long id, bool multiInstance, bool inputs = true)
    {
        var candidate = new AdministrativeActionCandidateDto(
            multiInstance ? AdministrativeActionPositionKinds.MultiInstanceExecution : AdministrativeActionPositionKinds.UserTask,
            id, multiInstance ? null : id, multiInstance ? id : null, 42, 12, Guid.NewGuid(), 8, 3, "approval", null, 7,
            "Approval", null, DateTimeOffset.Parse("2026-09-10T10:00:00Z"), multiInstance ? 6 : 1, []);
        IReadOnlyList<VariableModel> variables = inputs ?
        [
            new() { Name = "approved", DataType = WorkflowVariableTypes.Boolean, Required = true },
            new() { Name = "amounts", DataType = WorkflowVariableTypes.Number, IsArray = true, Required = true },
            new() { Name = "context", DataType = WorkflowVariableTypes.Json }
        ] : [];
        return new(candidate, [new AdministrativeActionSummaryDto(8, 3, AdministrativeActionKinds.DirectFlow, 14, null,
            "Approve", 7, "Approval", 9, "Finished", BpmnFlowNodeTypes.EndEvent, variables)]);
    }

    private static InstanceDetailDto CompletedInstance()
    {
        var now = DateTimeOffset.Parse("2026-09-10T10:00:00Z");
        var workflow = new WorkflowDetailDto(8, "Approval", "approval", 3, true, true, now,
            new WorkflowModel { Id = "approval", Name = "Approval" });
        return new InstanceDetailDto(42, workflow, 9, "Finished", null, "completed", null, null, "starter", now, now, [], [], null, null, null);
    }

    private static HttpResponseMessage Response(HttpStatusCode code, object body) => new(code) { Content = JsonContent.Create(body) };

    private static HttpResponseMessage BatchCatalogResponse(long definitionId) => Response(HttpStatusCode.OK,
        new[] { new WorkflowSummaryDto(definitionId, definitionId == 8 ? "Old identity workflow" : "Current workflow",
            "approval", 3, true, true, DateTimeOffset.Parse("2026-09-10T10:00:00Z")) });

    private static HttpResponseMessage BatchDiscoveryResponse(string path, long definitionId)
    {
        var name = definitionId == 8 ? "Old identity task" : "Current task";
        var item = Position(74, multiInstance: false, inputs: false);
        object result = path switch
        {
            _ when path.EndsWith("/administrative-actions/nodes", StringComparison.Ordinal) =>
                new[] { new AdministrativeActionSourceNodeDto(definitionId, 3, 7, name, null, false) },
            _ when path.Contains("/nodes/7/administrative-actions", StringComparison.Ordinal) =>
                new[] { item.Actions[0] with { WorkflowDefinitionId = definitionId, SourceNodeName = name,
                    Name = definitionId == 8 ? "Old identity approval" : "Current approval" } },
            "/api/administrative-actions/candidates/search" =>
                new PagedResult<AdministrativeActionCandidateDto>([item.Position with { WorkflowDefinitionId = definitionId, NodeName = name }], 1, 25, 1),
            "/api/administrative-action-batches" => new PagedResult<AdministrativeActionBatchSummaryDto>([], 1, 25, 0),
            _ => throw new InvalidOperationException($"Unexpected request {path}")
        };
        return Response(HttpStatusCode.OK, result);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private readonly CapturingActivator activator = new();
        private readonly HttpClient http;
        public TokenState Token { get; } = new();
        public List<string> Paths { get; } = [];

        public Harness(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            Token.Set("admin-token");
            http = new HttpClient(new Handler(request => { Paths.Add(request.RequestUri!.PathAndQuery); return respond(request); }))
            { BaseAddress = new Uri("https://flowbit.test") };
            var services = new ServiceCollection();
            services.AddLogging(); services.AddSingleton(new WorkflowApiClient(http)); services.AddSingleton(Token);
            services.AddSingleton<IComponentActivator>(activator);
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent =>
            renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<T>(parameters));
        public Task<string> HtmlAsync(HtmlRootComponent component) => renderer.Dispatcher.InvokeAsync(component.ToHtmlString);
        public Task DispatchAsync(Action action) => renderer.Dispatcher.InvokeAsync(action);
        public Task<T> BatchValueAsync<T>(string name) => renderer.Dispatcher.InvokeAsync(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            return (T)(typeof(BatchPage).GetField(name, flags)?.GetValue(activator.Batch)
                ?? typeof(BatchPage).GetProperty(name, flags)!.GetValue(activator.Batch))!;
        });
        public async Task WaitForBatchValueAsync(string name, bool expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await BatchValueAsync<bool>(name) != expected) await Task.Delay(10, timeout.Token);
        }
        public Task SetAsync(string field, object value) => DispatchAsync(() =>
            typeof(InstancePanel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activator.Panel, value));
        public Task CallAsync(string method, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(InstancePanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Panel, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Panel, null);
        });
        public Task CallBatchAsync(string method, params object[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(BatchPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Batch, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Batch, null);
        });
        public async ValueTask DisposeAsync() { await renderer.DisposeAsync(); await provider.DisposeAsync(); http.Dispose(); }
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public InstancePanel? Panel { get; private set; }
        public BatchPage? Batch { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is InstancePanel panel) Panel = panel;
            if (component is BatchPage batch) Batch = batch;
            return component;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
