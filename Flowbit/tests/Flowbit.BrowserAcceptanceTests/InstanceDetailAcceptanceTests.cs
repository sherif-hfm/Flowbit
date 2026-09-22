using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// Stage 5 acceptance using real HTTP-created catalog entries, durable batches,
/// delegations and task actions. Screenshots are review evidence, not pixel baselines.
/// </summary>
[Collection(AcceptanceCollection.Name)]
public sealed class InstanceDetailAcceptanceTests(AcceptanceFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AdministratorRoles = ["admin", "Reviewer", "Auditor"];
    private BrowserStackFixture Stack => fixture.Stack;

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task SharedBindingsAndWorkerBatchHistories(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"stage05-audits-{width}x{height}", width, height, scenarioTimeout: TimeSpan.FromMinutes(4));
        await scenario.RunAsync("catalog-and-durable-audits", async () =>
        {
            await Stack.StartWorkerAsync();
            var model = await CreateAuditModelAsync();
            var first = await Stack.SetupClient.CreateAndPublishAsync(model);
            model.Name = "Instance detail acceptance v2";
            var second = await Stack.SetupClient.CreateAndPublishAsync(model);
            Assert.Equal(first.WorkflowKey, second.WorkflowKey);
            Assert.Equal(first.Version + 1, second.Version);
            var instance = await Stack.SetupClient.StartInstanceAsync(first.Id);
            await using var preparer = await ApplyActorAsync(scenario, "release-preparer", AdministratorRoles);
            await using var confirmer = await ApplyActorAsync(scenario, "release-confirmer", AdministratorRoles);

            var upgrade = await ChangeVersionAsync(preparer, confirmer, instance.Id, first.Id, second.Id,
                "Upgrade <reviewed> & approved");
            var downgrade = await ChangeVersionAsync(preparer, confirmer, instance.Id, second.Id, first.Id,
                "Return to original reviewed release");
            var updateOne = await UpdateVariableAsync(preparer, confirmer, model.Id, instance.Id,
                "First corrected value", "First variable correction");
            var updateTwo = await UpdateVariableAsync(preparer, confirmer, model.Id, instance.Id,
                "Final corrected value", "Second correction <checked> & retained");

            var detail = await confirmer.GetInstanceAsync(instance.Id);
            Assert.Equal(first.Id, detail.Workflow.Id);
            Assert.Equal(2, detail.VersionChanges.Count);
            Assert.Equal(2, detail.VariableUpdates.Count);
            var binding = Assert.Single(detail.SharedVariables);
            Assert.Equal("catalogLabel", binding.Alias);
            Assert.Equal(model.Variables.Single(variable => variable.Name == "catalogLabel").SharedKey, binding.Key);
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            var page = scenario.Page;
            await WaitForInteractiveAsync(page);

            var shared = page.Locator("#shared-variables");
            await Assertions.Expect(shared).ToContainTextAsync(binding.Key);
            await Assertions.Expect(shared).ToContainTextAsync("Read only");
            await Assertions.Expect(shared).ToContainTextAsync("Value set");
            await Assertions.Expect(shared).ToContainTextAsync($"Revision #{binding.Revision}");
            await Assertions.Expect(page.Locator("body")).Not.ToContainTextAsync("CATALOG_VALUE_MUST_NOT_LEAK");

            var versions = page.Locator("#version-changes tbody tr");
            await Assertions.Expect(versions).ToHaveCountAsync(2);
            var orderedChanges = detail.VersionChanges.OrderByDescending(item => item.ChangedAt)
                .ThenByDescending(item => item.Id).ToArray();
            for (var index = 0; index < orderedChanges.Length; index++)
            {
                var change = orderedChanges[index];
                await Assertions.Expect(versions.Nth(index)).ToContainTextAsync(change.Reason);
                await Assertions.Expect(versions.Nth(index)).ToContainTextAsync("release-confirmer");
                await Assertions.Expect(versions.Nth(index)).ToContainTextAsync("Auditor");
                await Assertions.Expect(versions.Nth(index).Locator("td").Nth(2)).ToContainTextAsync($"v{change.SourceWorkflow.Version}");
                await Assertions.Expect(versions.Nth(index).Locator("td").Nth(3)).ToContainTextAsync($"v{change.TargetWorkflow.Version}");
                await Assertions.Expect(versions.Nth(index).Locator("a"))
                    .ToHaveAttributeAsync("href", $"instance-version-changes?batchId={change.BatchId}");
            }
            var updates = page.Locator("#variable-updates tbody tr");
            await Assertions.Expect(updates).ToHaveCountAsync(2);
            var orderedUpdates = detail.VariableUpdates.OrderByDescending(item => item.PerformedAt)
                .ThenByDescending(item => item.Id).ToArray();
            for (var index = 0; index < orderedUpdates.Length; index++)
            {
                var update = orderedUpdates[index];
                await Assertions.Expect(updates.Nth(index)).ToContainTextAsync(update.Reason!);
                await Assertions.Expect(updates.Nth(index)).ToContainTextAsync("release-confirmer");
                await Assertions.Expect(updates.Nth(index)).ToContainTextAsync("Auditor");
                await Assertions.Expect(updates.Nth(index)).ToContainTextAsync(update.Variables.Single().Value.GetString()!);
                await Assertions.Expect(updates.Nth(index).Locator("a"))
                    .ToHaveAttributeAsync("href", $"instance-variable-updates?batchId={update.BatchId}");
            }
            await Assertions.Expect(page.Locator("#variables")).ToContainTextAsync("Final corrected value");
            var auditLink = page.Locator("#variables tbody tr")
                .Filter(new LocatorFilterOptions { HasText = "Final corrected value" })
                .Locator("a[href='#variable-updates']");
            await Assertions.Expect(auditLink).ToHaveTextAsync($"#{orderedUpdates[0].Id}");
            await auditLink.FocusAsync();
            await auditLink.PressAsync("Enter");
            await AssertSectionVisibleInViewportAsync(page, "variable-updates");
            foreach (var section in new[] { "shared-variables", "version-changes", "variable-updates", "variables", "history" })
                await NavigateSectionAsync(page, section);
            await CaptureAsync(scenario, "shared-bindings-and-audits", width, height);

            foreach (var batchId in new[] { downgrade, upgrade })
                await FollowBatchAndBackAsync(page, instance.Id, "version-changes", "instance-version-changes",
                    "version-current-batch-heading", batchId);
            foreach (var batchId in new[] { updateTwo, updateOne })
                await FollowBatchAndBackAsync(page, instance.Id, "variable-updates", "instance-variable-updates",
                    "variable-update-current-heading", batchId);

            await page.Locator("#shared-variables a.section-link").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/shared-variables$"));
            await page.GoBackAsync();
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#shared-variables")).ToContainTextAsync(binding.Key);
            await WriteEvidenceAsync(scenario, new { instanceId = instance.Id, first, second, binding,
                versionBatches = new[] { upgrade, downgrade }, variableBatches = new[] { updateOne, updateTwo } });
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task RealClaimAndDelegationAttribution(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"stage05-attribution-{width}x{height}", width, height);
        await scenario.RunAsync("claim-and-delegated-action", async () =>
        {
            var model = await CreateAuditModelAsync();
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await Stack.SetupClient.StartInstanceAsync(workflow.Id);
            var taskId = (await Stack.SetupClient.GetInstanceAsync(instance.Id)).UserTasks!.SoleUserTaskId!.Value;
            var ownerClaims = new Dictionary<string, string> { ["department"] = "Owner <Finance & Review>",
                ["region"] = "Owner region", ["notSelected"] = "SHOULD_NOT_BE_CAPTURED" };
            await using var owner = await ApplyActorAsync(scenario, "acceptance-owner", ["Reviewer"], ownerClaims);
            await scenario.OpenUiAsync($"user-tasks/{taskId}");
            var page = scenario.Page;
            await WaitForInteractiveAsync(page);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Claim task", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unclaim", Exact = true }))
                .ToBeVisibleAsync();
            var now = DateTimeOffset.UtcNow;
            var grant = Assert.Single(await owner.CreateDelegationAsync(new CreateUserDelegationRequest(
                "acceptance-delegate", [model.Id], now.AddMinutes(-1), now.AddHours(1), "Cover the review")));
            var delegateClaims = new Dictionary<string, string> { ["department"] = "Delegate <Operations & Review>",
                ["region"] = "Delegate region", ["notSelected"] = "SHOULD_NOT_BE_CAPTURED" };
            await using var delegated = await ApplyActorAsync(scenario, "acceptance-delegate", ["Reviewer"], delegateClaims);
            if (!grant.IsActive)
                grant = await delegated.AcceptDelegationAsync(grant.Id, new UserDelegationLifecycleRequest(grant.UpdatedAt));
            Assert.True(grant.IsActive);
            await scenario.OpenUiAsync($"user-tasks/{taskId}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator(".acting-for-badge").First).ToContainTextAsync("acceptance-owner");
            await page.Locator($"#task-{taskId}-flow-102-decisionNote").FillAsync("Reviewed by the covering delegate");
            await page.Locator(".action-card", new PageLocatorOptions { HasText = "Approve" })
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action", Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/instances/{instance.Id}$"));
            await Assertions.Expect(page.Locator("#variables")).ToContainTextAsync("Reviewed by the covering delegate");
            var detail = await delegated.GetInstanceAsync(instance.Id);
            Assert.Equal("completed", detail.Status);
            var claim = Assert.Single(detail.History, item => item.Note == "taskClaim");
            Assert.Equal("acceptance-owner", claim.PerformedBy);
            Assert.Equal(new[] { ownerClaims["department"] }, claim.ActorClaims!["department"]);
            var action = Assert.Single(detail.History, item => item.SequenceFlowId == 102);
            Assert.Equal("acceptance-delegate", action.PerformedBy);
            Assert.Equal("acceptance-owner", action.ActingFor);
            Assert.Equal(grant.Id, action.DelegationId);
            Assert.Equal(new[] { delegateClaims["department"] }, action.ActorClaims!["department"]);
            Assert.DoesNotContain("notSelected", action.ActorClaims.Keys);

            var claimRow = page.Locator("#history tbody tr").Filter(new LocatorFilterOptions { HasText = "Claimed by acceptance-owner" });
            await Assertions.Expect(claimRow).ToHaveCountAsync(1);
            var actionRow = page.Locator("#history tbody tr").Filter(new LocatorFilterOptions
            {
                Has = page.Locator(".acting-for-badge"),
            });
            await Assertions.Expect(actionRow).ToHaveCountAsync(1);
            await Assertions.Expect(actionRow).ToContainTextAsync("acceptance-delegate");
            await Assertions.Expect(actionRow.Locator(".acting-for-badge"))
                .ToHaveAttributeAsync("title", $"Delegated task access via grant #{grant.Id}");
            foreach (var row in new[] { claimRow, actionRow })
            {
                await row.Locator("details.actor-claims summary").FocusAsync();
                await row.Locator("details.actor-claims summary").PressAsync("Enter");
            }
            await Assertions.Expect(claimRow.Locator(".actor-claim-value").First).ToHaveTextAsync(ownerClaims["department"]);
            await Assertions.Expect(actionRow.Locator(".actor-claim-value").First).ToHaveTextAsync(delegateClaims["department"]);
            await Assertions.Expect(page.Locator("#history")).Not.ToContainTextAsync("SHOULD_NOT_BE_CAPTURED");
            await Assertions.Expect(page.Locator("#history script")).ToHaveCountAsync(0);
            await NavigateSectionAsync(page, "history");
            await CaptureAsync(scenario, "claims-and-delegation", width, height);
            await WriteEvidenceAsync(scenario, new { instanceId = instance.Id, taskId, delegationId = grant.Id,
                claimHistoryId = claim.Id, actionHistoryId = action.Id });
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task RepresentativeStatesAndSectionNavigation(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"stage05-states-{width}x{height}", width, height, scenarioTimeout: TimeSpan.FromMinutes(4));
        await scenario.RunAsync("empty-normal-terminal-gateway-mi", async () =>
        {
            await using var reviewer = await ApplyActorAsync(scenario, "state-reviewer", AdministratorRoles);
            var navigation = await Stack.SetupClient.CreateAndPublishAsync(SmokeFixture("runtime-navigation.json"));
            var empty = await Stack.SetupClient.StartInstanceAsync(navigation.Id);
            await scenario.OpenUiAsync($"instances/{empty.Id}");
            var page = scenario.Page;
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#variables")).ToContainTextAsync("No variables captured.");
            await Assertions.Expect(page.Locator("#actions .card")).ToHaveCountAsync(1);
            foreach (var section in new[] { "variables", "history", "actions" })
                await NavigateSectionAsync(page, section);
            await CaptureAsync(scenario, "new-empty-instance", width, height);
            await reviewer.TakeInstanceFlowAsync(empty.Id, 102);
            await scenario.OpenUiAsync($"instances/{empty.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("approval2");
            await CaptureAsync(scenario, "normal-user-task", width, height);
            await reviewer.TakeInstanceFlowAsync(empty.Id, 103);
            await scenario.OpenUiAsync($"instances/{empty.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#actions")).ToContainTextAsync("No user actions are available.");
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Completed normally");
            await CaptureAsync(scenario, "terminal-instance", width, height);

            var gateway = await Stack.SetupClient.CreateAndPublishAsync(SmokeFixture("runtime-gateway.json"));
            var gatewayInstance = await Stack.SetupClient.StartInstanceAsync(gateway.Id);
            foreach (var task in await reviewer.GetInboxAsync(gatewayInstance.Id))
                await reviewer.TakeUserTaskFlowAsync(task.UserTaskId,
                    Assert.Single(await reviewer.GetUserTaskFlowsAsync(task.UserTaskId)).Id);
            var gatewayDetail = await reviewer.GetInstanceAsync(gatewayInstance.Id);
            Assert.Equal("Finalize", gatewayDetail.CurrentNodeName);
            await scenario.OpenUiAsync($"instances/{gatewayInstance.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#gateway-scopes tbody tr").First).ToContainTextAsync("Merge after both (#5)");
            await Assertions.Expect(page.Locator("#gateway-scopes")).ToContainTextAsync("Fork reviews (#2)");
            foreach (var execution in gatewayDetail.GatewayExecutions)
            {
                var row = page.Locator("#gateway-scopes tbody tr")
                    .Filter(new LocatorFilterOptions { HasText = $"(#{execution.GatewayNodeId})" });
                await Assertions.Expect(row.Locator("td").Nth(2)).ToHaveTextAsync(Title(execution.Status));
            }
            var complex = Assert.Single(gatewayDetail.ComplexGatewayStates);
            await Assertions.Expect(page.Locator("#complex-gateway-states")).ToContainTextAsync("#301, #401");
            await Assertions.Expect(page.Locator("#complex-gateway-states tbody tr td").Nth(1)).ToHaveTextAsync(Title(complex.Phase));
            await Assertions.Expect(page.Locator("#complex-gateway-states tbody tr td").Nth(2)).ToHaveTextAsync(complex.Cycle.ToString());
            await NavigateSectionAsync(page, "gateway-scopes");
            await NavigateSectionAsync(page, "complex-gateway-states");
            await CaptureAsync(scenario, "gateway-complex-state", width, height);

            var mi = await Stack.SetupClient.CreateAndPublishAsync(SmokeFixture("runtime-mi.json"));
            var miInstance = await Stack.SetupClient.StartInstanceAsync(mi.Id);
            await scenario.OpenUiAsync($"instances/{miInstance.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#multi-instance-results")).ToContainTextAsync("No work items completed yet.");
            await CaptureAsync(scenario, "mi-empty-results", width, height);
            await using var beta = await ApplyActorAsync(scenario, "beta", ["Reviewer"]);
            var betaTask = Assert.Single(await beta.GetInboxAsync(miInstance.Id));
            await beta.TakeUserTaskFlowAsync(betaTask.UserTaskId, 201,
                new() { ["reviewComment"] = JsonSerializer.SerializeToElement("Reviewed by beta") });
            await using var gamma = await ApplyActorAsync(scenario, "gamma", ["Reviewer"]);
            var gammaTask = Assert.Single(await gamma.GetInboxAsync(miInstance.Id));
            await gamma.TakeUserTaskFlowAsync(gammaTask.UserTaskId, 201,
                new() { ["reviewComment"] = JsonSerializer.SerializeToElement("Reviewed by gamma") });
            await scenario.OpenUiAsync($"instances/{miInstance.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator(".summary-item", new PageLocatorOptions { HasText = "Multi-instance" }))
                .ToContainTextAsync("2 / 3 completed");
            var rows = page.Locator("#multi-instance-results tbody tr");
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await Assertions.Expect(rows.Nth(0)).ToContainTextAsync("gamma");
            await Assertions.Expect(rows.Nth(1)).ToContainTextAsync("beta");
            await Assertions.Expect(rows.Nth(0)).ToContainTextAsync("Reviewed by gamma");
            await Assertions.Expect(rows.Nth(1)).ToContainTextAsync("Complete review (#201)");
            await NavigateSectionAsync(page, "multi-instance-results");
            await CaptureAsync(scenario, "mi-submitted-results", width, height);

            // An early quorum keeps the completed evidence while cancelling
            // unfinished children; the extracted result section must not
            // fabricate completed rows for those cancelled work items.
            var quorumModel = JsonSerializer.Deserialize<WorkflowModel>(
                await File.ReadAllTextAsync(SmokeFixture("runtime-mi.json")), JsonOptions)!;
            quorumModel.Id = $"acceptance-mi-quorum-{Guid.NewGuid():N}";
            quorumModel.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition = "CountFlow(201) >= 1";
            var quorumWorkflow = await Stack.SetupClient.CreateAndPublishAsync(quorumModel);
            var quorumInstance = await Stack.SetupClient.StartInstanceAsync(quorumWorkflow.Id);
            var quorumTask = Assert.Single(await gamma.GetInboxAsync(quorumInstance.Id));
            await gamma.TakeUserTaskFlowAsync(quorumTask.UserTaskId, 201,
                new() { ["reviewComment"] = JsonSerializer.SerializeToElement("Quorum reached by gamma") });
            var quorumDetail = await gamma.GetInstanceAsync(quorumInstance.Id);
            Assert.Equal("completed", quorumDetail.Status);
            Assert.Equal(2, Assert.Single(quorumDetail.MultiInstances).Cancelled);
            await scenario.OpenUiAsync($"instances/{quorumInstance.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#multi-instance-results tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#multi-instance-results")).ToContainTextAsync("Quorum reached by gamma");
            await Assertions.Expect(page.Locator("#variables")).ToContainTextAsync("cancelled");
            await CaptureAsync(scenario, "mi-completed-with-cancelled-children", width, height);
            await WriteEvidenceAsync(scenario, new { emptyAndTerminalInstanceId = empty.Id,
                gatewayInstanceId = gatewayInstance.Id, multiInstanceId = miInstance.Id,
                quorumInstanceId = quorumInstance.Id });
        });
    }

    private async Task<WorkflowModel> CreateAuditModelAsync()
    {
        var model = JsonSerializer.Deserialize<WorkflowModel>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "instance-detail-audit.json")), JsonOptions)!;
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = $"acceptance-instance-detail-{suffix}";
        var key = $"acceptance.catalog.{suffix}";
        await Stack.SetupClient.CreateSharedVariableAsync(new CreateSharedVariableRequest(
            key, "string", false, false, true, JsonSerializer.SerializeToElement("CATALOG_VALUE_MUST_NOT_LEAK"),
            Description: "Catalog metadata for real instance-detail acceptance"));
        model.Variables.Single(variable => variable.Name == "catalogLabel").SharedKey = key;
        return model;
    }

    private async Task<WorkflowFixtureClient> ApplyActorAsync(BrowserScenario scenario, string user, string[] roles,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        var identity = new IdentityScreen(scenario.Page, Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(user, roles, claims);
        scenario.OnCleanup(identity.ClearIdentityAsync);
        return Stack.CreateClient(user, roles, token, $"{scenario.Name}-{user}");
    }

    private static async Task<long> ChangeVersionAsync(WorkflowFixtureClient preparer, WorkflowFixtureClient confirmer,
        long instanceId, long sourceId, long targetId, string reason)
    {
        var batch = await preparer.CreateVersionChangeBatchAsync(new CreateInstanceVersionChangeBatchRequest(
            sourceId, targetId, reason, new InstanceVersionChangeBatchSelectionDto("explicit", [instanceId], null, null),
            $"acceptance-{Guid.NewGuid():N}"));
        var ready = await AwaitStateAsync(() => preparer.GetVersionChangeBatchAsync(batch.Summary.Id), value => value.Summary.Status, "ready");
        Assert.Equal(1, ready.Summary.EligibleItemCount);
        await confirmer.ConfirmVersionChangeBatchAsync(batch.Summary.Id, new ConfirmInstanceVersionChangeBatchRequest(
            ready.Summary.EligibleItemCount, ready.Summary.IneligibleItemCount, ready.Summary.WarningItemCount, ready.Summary.UpdatedAt));
        var completed = await AwaitStateAsync(() => confirmer.GetVersionChangeBatchAsync(batch.Summary.Id), value => value.Summary.Status, "completed");
        Assert.Equal(1, completed.Summary.SucceededItemCount);
        return batch.Summary.Id;
    }

    private static async Task<long> UpdateVariableAsync(WorkflowFixtureClient preparer, WorkflowFixtureClient confirmer,
        string workflowKey, long instanceId, string value, string reason)
    {
        var batch = await preparer.CreateVariableUpdateBatchAsync(new CreateInstanceVariableUpdateBatchRequest(
            workflowKey, [new InstanceVariableWriteDto("reviewNote", JsonSerializer.SerializeToElement(value))], reason,
            new InstanceVariableUpdateBatchSelectionDto("explicit", [instanceId], null, null), $"acceptance-{Guid.NewGuid():N}"));
        var ready = await AwaitStateAsync(() => preparer.GetVariableUpdateBatchAsync(batch.Summary.Id), item => item.Summary.Status, "ready");
        Assert.Equal(1, ready.Summary.EligibleItemCount);
        await confirmer.ConfirmVariableUpdateBatchAsync(batch.Summary.Id, new ConfirmInstanceVariableUpdateBatchRequest(
            ready.Summary.EligibleItemCount, ready.Summary.IneligibleItemCount, ready.Summary.WarningItemCount, ready.Summary.UpdatedAt));
        var completed = await AwaitStateAsync(() => confirmer.GetVariableUpdateBatchAsync(batch.Summary.Id), item => item.Summary.Status, "completed");
        Assert.Equal(1, completed.Summary.SucceededItemCount);
        return batch.Summary.Id;
    }

    private static async Task<T> AwaitStateAsync<T>(Func<Task<T>> read, Func<T, string> status, string expected)
    {
        var timer = Stopwatch.StartNew();
        var current = await read();
        while (status(current) != expected && timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            Assert.DoesNotContain(status(current), new[] { "failed", "cancelled", "completed" });
            await Task.Delay(100);
            current = await read();
        }
        Assert.Equal(expected, status(current));
        return current;
    }

    private static async Task WaitForInteractiveAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".app-shell")).ToBeVisibleAsync();
        var hasMarkers = await page.Locator(".app-shell").GetAttributeAsync("data-interactive") is not null;
        if (hasMarkers)
            await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = 30_000 });
        if (Regex.IsMatch(new Uri(page.Url).AbsolutePath, @"^/instances/\d+$"))
        {
            var summary = page.Locator("section[aria-labelledby='instance-summary-heading']");
            if (hasMarkers)
                await Assertions.Expect(summary).ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = 30_000 });
            else
                await Assertions.Expect(summary).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }
    }

    private static async Task FollowBatchAndBackAsync(IPage page, long instanceId, string section, string route, string heading, long batchId)
    {
        var instanceUrl = page.Url;
        Assert.Equal($"/instances/{instanceId}", new Uri(instanceUrl).AbsolutePath);
        await page.Locator($"#{section} a[href='{route}?batchId={batchId}']").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/{route}\\?batchId={batchId}$"));
        await WaitForInteractiveAsync(page);
        await Assertions.Expect(page.Locator($"section[aria-labelledby='{heading}']"))
            .ToContainTextAsync($"Batch #{batchId}");
        await Assertions.Expect(page.Locator($"section[aria-labelledby='{heading}']"))
            .ToContainTextAsync("Completed");
        await page.GoBackAsync();
        await Assertions.Expect(page).ToHaveURLAsync(instanceUrl);
        await WaitForInteractiveAsync(page);
        await Assertions.Expect(page.Locator($"#{section}")).ToBeVisibleAsync();
    }

    private static async Task NavigateSectionAsync(IPage page, string section)
    {
        var tab = page.Locator($".status-tab[href='#{section}']");
        await tab.FocusAsync();
        await tab.PressAsync("Enter");
        await AssertSectionVisibleInViewportAsync(page, section);
    }

    private static async Task AssertSectionVisibleInViewportAsync(IPage page, string section) =>
        await page.WaitForFunctionAsync("""
            id => { const r = document.getElementById(id)?.getBoundingClientRect();
                return r && r.top >= -2 && r.top < window.innerHeight - 20; }
            """, section, new() { Timeout = 10_000 });

    private static async Task CaptureAsync(BrowserScenario scenario, string state, int width, int height)
    {
        var page = scenario.Page;
        Assert.True(await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
            $"The {state} document overflows at {width}x{height}.");
        // Section navigation leaves focus on an anchor. Scroll the document
        // beneath a noninteractive heading so keyboard focus cannot consume
        // the capture reset or move it back to that section.
        await page.Locator("h1").HoverAsync();
        await page.Mouse.WheelAsync(0, -await page.EvaluateAsync<int>(
            "() => document.documentElement.scrollHeight"));
        await page.WaitForFunctionAsync("() => window.scrollY <= 1");
        await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, $"{state}-{width}x{height}.png"), FullPage = true });
        foreach (var container in await page.Locator(".table-responsive").AllAsync())
        {
            if (!await container.IsVisibleAsync() || !await container.EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth + 2")) continue;
            await container.ScrollIntoViewIfNeededAsync();
            await container.HoverAsync();
            await page.Mouse.WheelAsync(10_000, 0);
            await Assertions.Expect(container.Locator("th").Last).ToBeInViewportAsync();
            Assert.True(await container.EvaluateAsync<bool>("el => el.scrollLeft > 0"), "Clipped columns must be reachable with real horizontal wheel input.");
        }
    }

    private static string SmokeFixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static string Title(string value) => char.ToUpperInvariant(value[0]) + value[1..];
    private static Task WriteEvidenceAsync<T>(BrowserScenario scenario, T evidence) => File.WriteAllTextAsync(
        Path.Combine(scenario.ArtifactDirectory, "scenario-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
}
