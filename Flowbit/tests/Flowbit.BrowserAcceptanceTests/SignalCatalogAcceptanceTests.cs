using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// EVENT-04F, the signal catalog, through real API, UI and Worker processes. S16 publishes Example 01 under its own
/// family and signal name with waiting instances, creates an entry that fits from the Signal catalog page, and shows
/// that the live version protects its contract: a changed field type is refused with the version named, archiving is
/// refused with the wait count, and a throw from the Signals page that fits reaches every wait.
/// </summary>
public sealed partial class SignalAcceptanceTests
{
    /// <summary>S16: live versions protect their catalog contract.</summary>
    [Fact]
    public async Task S16_LiveVersionsProtectTheirCatalogContract()
    {
        await using var scenario = await Stack.CreateScenario("signal-s16-1440x900", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal catalog contract", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (workflow, signal) = await PublishExampleAsync("s16");
            var instances = await StartManyAsync(actor, workflow.Id, 3);
            var version = $"{workflow.WorkflowKey} v{workflow.Version} (#{workflow.Id})";
            var page = scenario.Page;

            // The published version uses the name without an entry; the page offers to create one, prefilled.
            await scenario.OpenUiAsync("signal-catalog");
            var uncataloged = page.Locator("section[aria-labelledby='signal-catalog-uncataloged-heading'] tbody tr",
                new PageLocatorOptions { HasText = signal });
            await Assertions.Expect(uncataloged).ToContainTextAsync("1 version");
            await uncataloged.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Create entry for {signal}", Exact = true })
                .ClickAsync();
            await Assertions.Expect(page.Locator("#signal-catalog-name")).ToHaveValueAsync(signal);

            // An entry that fits Example 01's mappings: version, a required number; effectiveDate, an optional date.
            // With the name filled in, the form opens with focus on the description.
            var form = page.Locator("section[aria-labelledby='signal-catalog-form-heading']");
            var addField = page.Locator("#signal-catalog-add-field");
            var description = page.Locator("#signal-catalog-description");
            await Assertions.Expect(description).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("A claims policy changed", new KeyboardTypeOptions { Delay = 15 });
            await Assertions.Expect(description).ToHaveValueAsync("A claims policy changed");
            await addField.ClickAsync();
            await Assertions.Expect(page.Locator("#signal-catalog-field-name-1")).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("version", new KeyboardTypeOptions { Delay = 15 });
            await page.Locator("#signal-catalog-field-type-1").SelectOptionAsync("number");
            await page.Locator("#signal-catalog-field-required-1").CheckAsync();
            await addField.ClickAsync();
            await Assertions.Expect(page.Locator("#signal-catalog-field-name-2")).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("effectiveDate", new KeyboardTypeOptions { Delay = 15 });
            await page.Locator("#signal-catalog-field-type-2").SelectOptionAsync("date");
            await Assertions.Expect(page.Locator("#signal-catalog-field-name-1")).ToHaveValueAsync("version");
            await Assertions.Expect(page.Locator("#signal-catalog-field-name-2")).ToHaveValueAsync("effectiveDate");
            await page.Locator("#signal-catalog-review").ClickAsync();
            await Assertions.Expect(form.Locator(".signal-catalog-review-fits"))
                .ToHaveTextAsync("The live workflow versions that use this name all fit.");
            await page.Locator("#signal-catalog-save").ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success")).ToHaveTextAsync($"Signal catalog entry {signal} added.");
            await Assertions.Expect(uncataloged).ToHaveCountAsync(0);
            var row = page.Locator("table.signal-catalog-table tbody tr", new PageLocatorOptions { HasText = signal });
            await Assertions.Expect(row).ToContainTextAsync("2 fields");

            // Changing the mapped field's type: the review names the live version and offers no Save.
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Edit {signal}", Exact = true }).ClickAsync();
            await page.Locator("#signal-catalog-field-type-1").SelectOptionAsync("string");
            await page.Locator("#signal-catalog-review").ClickAsync();
            var problem = form.Locator(".signal-catalog-problems li");
            var mismatch =
                $"Signal catch event #2 mapping to 'policyVersion' is number, but the catalog entry of '{signal}' defines 'version' as string.";
            await Assertions.Expect(problem).ToHaveTextAsync($"{version}: {mismatch}");
            await Assertions.Expect(problem.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = version, Exact = true }))
                .ToHaveAttributeAsync("href", $"workflows/{workflow.Id}/edit");
            await Assertions.Expect(page.Locator("#signal-catalog-save")).ToHaveCountAsync(0);
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s16-refused-change");

            // The API refuses the same change and names the version; the entry keeps its type.
            var entry = (await Stack.SetupClient.GetSignalCatalogEntryAsync(signal))!;
            using (var refused = await actor.Http.PutAsJsonAsync($"/api/signal-catalog/entries/{signal}",
                new UpdateSignalCatalogEntryRequest(entry.UpdatedAt, entry.Description,
                [
                    new SignalCatalogFieldDto("version", WorkflowVariableTypes.String, Required: true),
                    new SignalCatalogFieldDto("effectiveDate", WorkflowVariableTypes.Date),
                ], [.. entry.ThrowRoles], entry.AllowClients, entry.AllowWorkflows), JsonOptions, ScenarioCancellation.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains($"{version}: {mismatch}", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            Assert.Equal(WorkflowVariableTypes.Number, (await Stack.SetupClient.GetSignalCatalogEntryAsync(signal))!.Fields[0].DataType);
            await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(form).ToHaveCountAsync(0);

            // Archiving waits for the published version, the version with running instances, and the three waits.
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Archive {signal}", Exact = true }).ClickAsync();
            var archivePanel = page.Locator("section[aria-labelledby='signal-catalog-archive-heading']");
            var usage = archivePanel.Locator("dl.signal-catalog-usage");
            await Assertions.Expect(usage.Locator("dt")).ToHaveTextAsync(
            [
                "Published workflow versions that use it",
                "Workflow versions with a running instance that use it",
                "Open signal waits for it",
                "Workflow families that start on it",
                "Broadcasts still delivering",
            ]);
            await Assertions.Expect(usage.Locator("dd")).ToHaveTextAsync(["1", "1", "3", "0", "0"]);
            await Assertions.Expect(archivePanel).ToContainTextAsync("Archiving waits until every count above is zero.");
            await Assertions.Expect(archivePanel.Locator(".signal-catalog-versions li")).ToContainTextAsync($"{workflow.WorkflowKey} v{workflow.Version}");
            await Assertions.Expect(page.Locator("#signal-catalog-archive")).ToHaveCountAsync(0);
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s16-archive-refused");
            using (var refused = await actor.Http.PostAsJsonAsync($"/api/signal-catalog/entries/{signal}/archive",
                new SignalCatalogLifecycleRequest(entry.UpdatedAt), JsonOptions, ScenarioCancellation.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains("3 signal waits are open for it", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            Assert.Equal(SignalCatalogStatuses.Active, (await Stack.SetupClient.GetSignalCatalogEntryAsync(signal))!.Status);

            // A throw from the Signals page that fits reaches every wait.
            await scenario.OpenUiAsync("signals");
            await FillSignalAsync(page, signal);
            await Assertions.Expect(page.GetByText($"3 waits in 1 family: {workflow.WorkflowKey} (3)")).ToBeVisibleAsync();
            var panel = page.Locator("#signal-catalog-panel");
            await Assertions.Expect(panel.Locator(".signal-catalog-fields li"))
                .ToContainTextAsync(["version: a number, required", "effectiveDate: a date (YYYY-MM-DD), optional"]);
            await Assertions.Expect(panel.Locator(".signal-catalog-throw")).ToHaveTextAsync("You can throw this signal.");
            await page.Locator("#signal-workflow-key").FillAsync(workflow.WorkflowKey);
            await page.Locator("#signal-workflow-key").PressAsync("Tab");
            await page.Locator("#signal-payload").FillAsync("{ \"version\": 8, \"effectiveDate\": \"2026-12-01\" }");
            await page.Locator("#signal-payload").PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            var review = page.Locator("section[aria-labelledby='signal-review-heading']");
            await Assertions.Expect(review.Locator(".signal-review-catalog")).ToHaveTextAsync(signal);
            await Assertions.Expect(review).ToContainTextAsync("Reaches now3 waits");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success"))
                .ToContainTextAsync(new Regex(@"Broadcast #\d+ froze 3 recipients; the Worker is delivering it\."));
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current.Locator(".status-badge").First)
                .ToHaveTextAsync("Completed", new LocatorAssertionsToHaveTextOptions { Timeout = 90_000f });
            await Assertions.Expect(current).ToContainTextAsync("Delivered3");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s16-throw-delivered");
            foreach (var id in instances)
            {
                var detail = await actor.Runtime.GetInstanceAsync(id);
                Assert.Empty(detail.SignalSubscriptions);
                Assert.Single(detail.History, item => item.Note == "signalReceived");
                Assert.Contains(detail.ExecutionPositions, position => position.NodeId == 4 && position.TokenStatus == "active");
            }
        });
    }
}
