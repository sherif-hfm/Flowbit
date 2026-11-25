using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// EVENT-04F, the signal catalog, in a real browser. R19 drives the Signal catalog page: a settings administrator
/// creates an entry with three fields and a throw role, sees both warnings, reviews, saves, edits, archives, and
/// reactivates it, and a field name that breaks the rule is refused before any request. R20 drives the Signals page:
/// the name list and catalog panel, the payload template, the API's 400 for a misspelled member, and a catalog 403
/// that keeps the page's data. H19 drives the hosted editor's catalog block. The stack and its catalog are shared by
/// every scenario, so each one uses signal names of its own and archives the entries it created when nothing uses
/// them. The smoke stack has no Worker and publishes no signal definition; live versions are acceptance scenario S16.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class SignalCatalogSmokeTests(BrowserStackFixture stack)
{
    private const string ClientsWarning =
        "API clients with signals.throw can still throw this signal; their roles are not checked.";

    private const string RequiredRoleWarning =
        "Users need a role from Signals.RequiredRole as well as one of these roles. None of these roles is in Signals.RequiredRole, so only users who hold both can throw this signal; add the role there to let its holders throw it.";

    private const string AllFit = "The live workflow versions that use this name all fit.";

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R19_AdministratorsCreateReviewEditArchiveAndReactivateAnEntry(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"r19-signal-catalog-{width}x{height}", width, height);
        await scenario.RunAsync("signal-catalog-page", async () =>
        {
            var name = $"browser.r19-{width}-{Guid.NewGuid():N}"[..28];
            const string role = "browser-r19-recaller";
            try
            {
                await RuntimeSupport.ApplyIdentityAsync(scenario, "catalog-admin", ["admin"]);
                var page = await scenario.OpenUiAsync("signal-catalog");
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Signal catalog", Level = 1 }))
                    .ToBeVisibleAsync();
                var form = page.Locator("section[aria-labelledby='signal-catalog-form-heading']");
                var success = page.Locator(".alert-success");
                var row = page.Locator("table.signal-catalog-table tbody tr", new PageLocatorOptions { HasText = name });

                // The list searches for this scenario's name only, so other scenarios' entries never shift its row.
                var search = page.Locator("#signal-catalog-search");
                await search.ClickAsync();
                await page.Keyboard.TypeAsync(name, new KeyboardTypeOptions { Delay = 15 });
                await search.PressAsync("Tab");
                await page.Locator("form.signal-catalog-filters")
                    .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Search", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("section[aria-labelledby='signal-catalog-results-heading']"))
                    .ToContainTextAsync("No entries");

                // Add entry opens an empty form; the name and description are typed.
                var nameInput = page.Locator("#signal-catalog-name");
                var addEntry = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add entry", Exact = true }).First;
                await RuntimeSupport.ClickUntilAsync(
                    () => addEntry.ClickAsync(),
                    async () => await nameInput.CountAsync() == 1,
                    TimeSpan.FromSeconds(20));
                await Assertions.Expect(page.Locator("#signal-catalog-form-heading")).ToHaveTextAsync("Add signal catalog entry");
                // A new entry's form opens with focus on the name.
                await Assertions.Expect(nameInput).ToBeFocusedAsync();
                await page.Keyboard.TypeAsync(name, new KeyboardTypeOptions { Delay = 15 });
                await Assertions.Expect(nameInput).ToHaveValueAsync(name);
                await TypeIntoAsync(page, page.Locator("#signal-catalog-description"), "Products recalled from the warehouse");

                // A field whose name breaks the payload-name rule is refused by the form, before any request.
                var addField = page.Locator("#signal-catalog-add-field");
                await addField.ClickAsync();
                var firstField = page.Locator("#signal-catalog-field-name-1");
                await Assertions.Expect(firstField).ToBeFocusedAsync();
                await page.Keyboard.TypeAsync("recall number", new KeyboardTypeOptions { Delay = 15 });
                await Assertions.Expect(firstField).ToHaveValueAsync("recall number");
                var previewsBefore = PreviewRequestCount();
                await page.Locator("#signal-catalog-review").ClickAsync();
                await Assertions.Expect(form.Locator(".alert-danger")).ToHaveTextAsync(
                    "Field 1 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.");
                await Assertions.Expect(page.Locator("#signal-catalog-save")).ToHaveCountAsync(0);
                await page.WaitForTimeoutAsync(1_000);
                Assert.Equal(previewsBefore, PreviewRequestCount());

                // Three fields: a required string with a description, a number, and a string array.
                await firstField.PressAsync("Control+a");
                await page.Keyboard.TypeAsync("recallNumber", new KeyboardTypeOptions { Delay = 15 });
                await Assertions.Expect(firstField).ToHaveValueAsync("recallNumber");
                // Fixing the field clears the form's own problem; no review is needed for that.
                await Assertions.Expect(form.Locator(".alert-danger")).ToHaveCountAsync(0);
                await page.Locator("#signal-catalog-field-required-1").CheckAsync();
                await TypeIntoAsync(page, page.Locator("#signal-catalog-field-description-1"), "The recall's number");
                await addField.ClickAsync();
                await Assertions.Expect(page.Locator("#signal-catalog-field-name-2")).ToBeFocusedAsync();
                await page.Keyboard.TypeAsync("quantity", new KeyboardTypeOptions { Delay = 15 });
                await page.Locator("#signal-catalog-field-type-2").SelectOptionAsync("number");
                await addField.ClickAsync();
                await Assertions.Expect(page.Locator("#signal-catalog-field-name-3")).ToBeFocusedAsync();
                await page.Keyboard.TypeAsync("skus", new KeyboardTypeOptions { Delay = 15 });
                await page.Locator("#signal-catalog-field-array-3").CheckAsync();
                await Assertions.Expect(page.Locator("#signal-catalog-field-name-2")).ToHaveValueAsync("quantity");
                await Assertions.Expect(page.Locator("#signal-catalog-field-name-3")).ToHaveValueAsync("skus");

                // A throw role outside Signals.RequiredRole, with clients still allowed, shows both warnings.
                await TypeIntoAsync(page, page.Locator("#signal-catalog-roles"), role);
                await Assertions.Expect(form.Locator(".signal-catalog-warning")).ToHaveTextAsync([ClientsWarning, RequiredRoleWarning]);
                await AssertNoHorizontalOverflowAsync(page, width);
                await form.ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "signal-catalog-form.png"),
                });

                // Save is offered only after a clean review of the form as it is; an edit ends the review.
                var review = page.Locator("#signal-catalog-review");
                var save = page.Locator("#signal-catalog-save");
                var fits = form.Locator(".signal-catalog-review-fits");
                await review.ClickAsync();
                await Assertions.Expect(fits).ToHaveTextAsync(AllFit);
                await Assertions.Expect(save).ToHaveTextAsync("Add entry");
                var description = page.Locator("#signal-catalog-description");
                await description.PressAsync("End");
                await page.Keyboard.TypeAsync(".");
                await Assertions.Expect(save).ToHaveCountAsync(0);
                await Assertions.Expect(fits).ToHaveCountAsync(0);
                await review.ClickAsync();
                await Assertions.Expect(fits).ToHaveTextAsync(AllFit);
                await save.ClickAsync();
                await Assertions.Expect(success).ToHaveTextAsync($"Signal catalog entry {name} added.");
                await Assertions.Expect(form).ToHaveCountAsync(0);
                await Assertions.Expect(row).ToContainTextAsync("Products recalled from the warehouse.");
                await Assertions.Expect(row).ToContainTextAsync("3 fields");
                await Assertions.Expect(row).ToContainTextAsync(role);
                await Assertions.Expect(row).ToContainTextAsync("Active");

                // The API reads the entry back as typed.
                var saved = await stack.SetupClient.GetSignalCatalogEntryAsync(name);
                Assert.NotNull(saved);
                Assert.Equal("Products recalled from the warehouse.", saved!.Description);
                Assert.Equal(
                    [("recallNumber", "string", false, true, (string?)"The recall's number"), ("quantity", "number", false, false, null), ("skus", "string", true, false, null)],
                    saved.Fields.Select(field => (field.Name, field.DataType, field.IsArray, field.Required, field.Description)));
                Assert.Equal([role], saved.ThrowRoles);
                Assert.True(saved.AllowClients);
                Assert.True(saved.AllowWorkflows);
                await AssertNoHorizontalOverflowAsync(page, width);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "signal-catalog.png"),
                    FullPage = true,
                });

                // Editing keeps the name and changes the description after a review.
                await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Edit {name}", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#signal-catalog-form-heading")).ToHaveTextAsync($"Edit {name}");
                await Assertions.Expect(nameInput).ToHaveAttributeAsync("readonly", string.Empty);
                await Assertions.Expect(page.Locator("#signal-catalog-field-name-3")).ToHaveValueAsync("skus");
                await Assertions.Expect(page.Locator("#signal-catalog-roles")).ToHaveValueAsync(role);
                // An edit opens with focus on the description, since the name cannot change.
                await Assertions.Expect(description).ToBeFocusedAsync();
                await description.PressAsync("Control+a");
                await page.Keyboard.TypeAsync("Products recalled from the warehouse, by lot", new KeyboardTypeOptions { Delay = 15 });
                await Assertions.Expect(description).ToHaveValueAsync("Products recalled from the warehouse, by lot");
                await review.ClickAsync();
                await Assertions.Expect(fits).ToHaveTextAsync(AllFit);
                await Assertions.Expect(save).ToHaveTextAsync("Save changes");
                await save.ClickAsync();
                await Assertions.Expect(success).ToHaveTextAsync($"Signal catalog entry {name} updated.");
                await Assertions.Expect(row).ToContainTextAsync("Products recalled from the warehouse, by lot");

                // Nothing uses the name, so the archive panel offers Archive after a confirmation.
                await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Archive {name}", Exact = true }).ClickAsync();
                var archivePanel = page.Locator("section[aria-labelledby='signal-catalog-archive-heading']");
                await Assertions.Expect(archivePanel.Locator("#signal-catalog-archive-heading")).ToHaveTextAsync($"Archive {name}");
                await Assertions.Expect(archivePanel.Locator("dl.signal-catalog-usage dd")).ToHaveTextAsync(["0", "0", "0", "0", "0"]);
                await Assertions.Expect(archivePanel).ToContainTextAsync("Nothing uses this name now.");
                await AssertNoHorizontalOverflowAsync(page, width);
                scenario.ExpectDialogOnce(
                    "confirm",
                    $"Archive signal catalog entry '{name}'? The name can no longer be thrown or used by a saved workflow until it is reactivated.",
                    accept: true);
                await page.Locator("#signal-catalog-archive").ClickAsync();
                await Assertions.Expect(success).ToHaveTextAsync($"Signal catalog entry {name} archived.");
                await Assertions.Expect(archivePanel).ToHaveCountAsync(0);
                await Assertions.Expect(row).ToHaveCountAsync(0);

                // The Archived filter lists it; Reactivate returns it to the active list.
                await page.Locator("#signal-catalog-status").SelectOptionAsync("archived");
                await Assertions.Expect(row).ToContainTextAsync("Archived");
                await AssertNoHorizontalOverflowAsync(page, width);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "signal-catalog-archived.png"),
                    FullPage = true,
                });
                await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Reactivate {name}", Exact = true }).ClickAsync();
                await Assertions.Expect(success).ToHaveTextAsync($"Signal catalog entry {name} reactivated.");
                await Assertions.Expect(row).ToHaveCountAsync(0);
                await page.Locator("#signal-catalog-status").SelectOptionAsync("active");
                await Assertions.Expect(row).ToContainTextAsync("Active");
                await Assertions.Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Edit {name}", Exact = true }))
                    .ToBeVisibleAsync();
                Assert.Equal(SignalCatalogStatuses.Active, (await stack.SetupClient.GetSignalCatalogEntryAsync(name))!.Status);
                await AssertNoHorizontalOverflowAsync(page, width);
            }
            finally
            {
                await stack.SetupClient.ArchiveSignalCatalogEntryIfUnusedAsync(name);
            }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R20_SignalsPageShowsTheEntryInsertsItsTemplateAndKeepsItsDataOnARefusal(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"r20-signals-catalog-{width}x{height}", width, height);
        await scenario.RunAsync("signals-page-catalog", async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var recall = $"browser.r20-{width}-{suffix}";
            var restricted = $"browser.r20-held-{width}-{suffix}";
            const string dispatcherRole = "browser-r20-dispatcher";
            try
            {
                await stack.SetupClient.CreateSignalCatalogEntryAsync(new CreateSignalCatalogEntryRequest(
                    recall,
                    "Warehouse recall",
                    [
                        new SignalCatalogFieldDto("recallNumber", WorkflowVariableTypes.String, Required: true, Description: "The recall's number"),
                        new SignalCatalogFieldDto("quantity", WorkflowVariableTypes.Number),
                        new SignalCatalogFieldDto("skus", WorkflowVariableTypes.String, IsArray: true),
                    ]));
                await stack.SetupClient.CreateSignalCatalogEntryAsync(new CreateSignalCatalogEntryRequest(
                    restricted,
                    "Dispatch hold",
                    [new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String)],
                    [dispatcherRole]));

                // admin satisfies the default Signals.RequiredRole but holds no dispatcher role.
                await RuntimeSupport.ApplyIdentityAsync(scenario, "signal-operator", ["admin"]);
                var page = await scenario.OpenUiAsync("signals");
                await Assertions.Expect(page.Locator($"#signal-name-options option[value='{recall}']"))
                    .ToHaveAttributeAsync("label", "Warehouse recall");
                await Assertions.Expect(page.Locator($"#signal-name-options option[value='{restricted}']"))
                    .ToHaveAttributeAsync("label", "Dispatch hold");

                // The typed name has an entry: the panel shows its description and fields.
                var name = page.Locator("#signal-name");
                var panel = page.Locator("#signal-catalog-panel");
                await TypeSignalNameAsync(page, name, recall, panel.Locator(".signal-catalog-throw"));
                await Assertions.Expect(panel.Locator(".signal-catalog-description")).ToHaveTextAsync("Warehouse recall");
                await Assertions.Expect(panel.Locator(".signal-catalog-fields li")).ToContainTextAsync(
                    ["recallNumber: a string, required", "quantity: a number, optional", "skus: an array of strings, optional"]);
                await Assertions.Expect(panel.Locator(".signal-catalog-fields li").First).ToContainTextAsync("The recall's number");
                await Assertions.Expect(panel.Locator(".signal-catalog-throw")).ToHaveTextAsync("You can throw this signal.");
                await page.GetByLabel("All workflows", new PageGetByLabelOptions { Exact = true }).CheckAsync();

                // Insert payload template fills the empty box, required fields first, and then turns itself off.
                var payload = page.Locator("#signal-payload");
                var insert = page.Locator("#signal-insert-template");
                await Assertions.Expect(payload).ToHaveValueAsync(string.Empty);
                await Assertions.Expect(insert).ToBeEnabledAsync();
                await insert.ClickAsync();
                await Assertions.Expect(payload).Not.ToHaveValueAsync(string.Empty);
                var template = JsonNode.Parse(await payload.InputValueAsync())!.AsObject();
                Assert.Equal(["recallNumber", "quantity", "skus"], template.Select(member => member.Key));
                Assert.Equal(string.Empty, template["recallNumber"]!.GetValue<string>());
                Assert.Equal(0, template["quantity"]!.GetValue<int>());
                Assert.Empty(template["skus"]!.AsArray());
                await Assertions.Expect(insert).ToBeDisabledAsync();
                await AssertNoHorizontalOverflowAsync(page, width);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "signals-catalog-panel.png"),
                    FullPage = true,
                });

                // A misspelled member is the API's 400, shown as the throw's error.
                var review = page.Locator("section[aria-labelledby='signal-review-heading']");
                var error = page.Locator(".alert-danger");
                await payload.FillAsync("{ \"recallNumber\": \"R-20\", \"quantiy\": 3 }");
                await payload.PressAsync("Tab");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
                await Assertions.Expect(review.Locator(".signal-review-catalog")).ToHaveTextAsync(recall);
                await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
                await Assertions.Expect(error).ToContainTextAsync(
                    $"Signal '{recall}' payload member 'quantiy' is not a field of its catalog entry. Its fields: recallNumber (string), quantity (number), skus (string[]).");

                // A payload that fits is thrown.
                await payload.FillAsync("{ \"recallNumber\": \"R-20\", \"quantity\": 3, \"skus\": [\"K-1\"] }");
                await payload.PressAsync("Tab");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
                await Assertions.Expect(review.Locator(".signal-review-catalog")).ToHaveTextAsync(recall);
                await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
                var success = page.Locator(".alert-success");
                await Assertions.Expect(success).ToContainTextAsync(new Regex(@"Broadcast #\d+ found no waiting events; it completed with no deliveries\."));
                var broadcastId = Regex.Match(await success.InnerTextAsync(), @"#(\d+)").Groups[1].Value;
                var current = page.Locator("section[aria-labelledby='signal-current-heading']");
                var history = page.Locator("section[aria-labelledby='signal-history-heading']");
                await Assertions.Expect(current).ToContainTextAsync($"Broadcast #{broadcastId}");
                await Assertions.Expect(history.Locator("tr.table-active")).ToContainTextAsync(recall);

                // An entry whose role this administrator lacks: the panel says why, and the throw's 403 is shown as
                // the refusal of that throw while the page keeps the broadcast and its history.
                await TypeSignalNameAsync(page, name, restricted, panel.Locator(".signal-catalog-description", new LocatorLocatorOptions { HasText = "Dispatch hold" }));
                await Assertions.Expect(panel.Locator(".signal-catalog-throw")).ToHaveTextAsync(
                    $"You cannot throw this signal: it needs one of the roles its catalog entry lists: {dispatcherRole}.");
                await payload.FillAsync("{ \"reason\": \"Damaged pallet\" }");
                await payload.PressAsync("Tab");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
                await Assertions.Expect(review.Locator(".signal-review-catalog")).ToHaveTextAsync(restricted);
                await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
                await Assertions.Expect(error).ToContainTextAsync(
                    $"Throwing signal '{restricted}' needs one of the roles its catalog entry lists: {dispatcherRole}.");
                await Assertions.Expect(page.GetByText("Signal permission is required")).ToHaveCountAsync(0);
                await Assertions.Expect(name).ToHaveValueAsync(restricted);
                await Assertions.Expect(current).ToContainTextAsync($"Broadcast #{broadcastId}");
                await Assertions.Expect(history.Locator("tr.table-active")).ToContainTextAsync(recall);
                await Assertions.Expect(history.Locator("tr", new LocatorLocatorOptions { HasTextRegex = new Regex($"#{broadcastId}(?!\\d)") }))
                    .ToHaveCountAsync(1);
                await Assertions.Expect(history.Locator("tr", new LocatorLocatorOptions { HasText = restricted })).ToHaveCountAsync(0);
                await AssertNoHorizontalOverflowAsync(page, width);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "signals-catalog-refused.png"),
                    FullPage = true,
                });
            }
            finally
            {
                await stack.SetupClient.ArchiveSignalCatalogEntryIfUnusedAsync(recall);
                await stack.SetupClient.ArchiveSignalCatalogEntryIfUnusedAsync(restricted);
            }
        });
    }

    [Fact]
    public async Task H19_HostedEditorShowsTheCatalogEntryAddsItsFieldsAndSavesAgainstIt()
    {
        await using var scenario = await stack.CreateScenario("h19-hosted-signal-catalog", 1440, 900);
        await scenario.RunAsync("hosted-signal-catalog", async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var policy = $"browser.h19-{suffix}";
            var retired = $"browser.h19-retired-{suffix}";
            var peopleOnly = $"browser.h19-people-{suffix}";
            try
            {
                await stack.SetupClient.CreateSignalCatalogEntryAsync(new CreateSignalCatalogEntryRequest(
                    policy,
                    "Claims policy changed",
                    [
                        new SignalCatalogFieldDto("policyVersion", WorkflowVariableTypes.Number, Required: true),
                        new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String),
                        new SignalCatalogFieldDto("effective-date", WorkflowVariableTypes.Date),
                    ]));
                var archived = await stack.SetupClient.CreateSignalCatalogEntryAsync(new CreateSignalCatalogEntryRequest(retired, "Retired signal"));
                await stack.SetupClient.ArchiveSignalCatalogEntryAsync(retired, archived.UpdatedAt);
                await stack.SetupClient.CreateSignalCatalogEntryAsync(new CreateSignalCatalogEntryRequest(
                    peopleOnly, "Thrown only by people", AllowWorkflows: false));

                var json = JsonNode.Parse(await File.ReadAllTextAsync(
                    EditorInteractions.FixturePath("editor-signal-events.json")))!.AsObject();
                json["id"] = "hosted-signal-catalog-" + Guid.NewGuid().ToString("N");
                var model = json.Deserialize<WorkflowModel>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var workflow = await stack.SetupClient.CreateUnpublishedAsync(model);
                await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
                var page = await scenario.OpenUiAsync($"workflows/{workflow.Id}/edit");
                await page.WaitForSelectorAsync("[data-editor-ready='true']");
                var editor = page.Locator("#workflow-editor-root");
                var inspector = editor.Locator("#inspector");
                ILocator Labeled(string label) => inspector.GetByLabel(label, new LocatorGetByLabelOptions { Exact = true });
                async Task<ILocator> SuggestionsAsync() =>
                    editor.Locator("#" + await Labeled("Signal name").GetAttributeAsync("list"));
                await EditorInteractions.PinInspectorOpenAsync(page);

                // Task #2 becomes a signal catch; its name field offers the active entries with their descriptions.
                await EditorInteractions.SelectNodeAsync(page, 2);
                await EditorInteractions.SelectTypeAsync(page, "intermediateSignalCatchEvent");
                var catchSuggestions = await SuggestionsAsync();
                await Assertions.Expect(catchSuggestions.Locator($"option[value='{policy}']")).ToHaveAttributeAsync("label", "Claims policy changed");
                await Assertions.Expect(catchSuggestions.Locator($"option[value='{peopleOnly}']")).ToHaveCountAsync(1);
                await Assertions.Expect(catchSuggestions.Locator($"option[value='{retired}']")).ToHaveCountAsync(0);

                // The name's entry: its description and fields.
                await Labeled("Signal name").FillAsync(policy);
                await Labeled("Signal name").PressAsync("Tab");
                var catchBlock = editor.Locator("#signal-catalog-2");
                await Assertions.Expect(catchBlock.Locator(".signal-catalog-title")).ToHaveTextAsync("Signal catalog entry");
                await Assertions.Expect(catchBlock.Locator(".signal-catalog-description")).ToHaveTextAsync("Claims policy changed");
                await Assertions.Expect(catchBlock.Locator(".signal-catalog-fields li"))
                    .ToHaveTextAsync(["policyVersion number, required", "reason string, optional", "effective-date date, optional"]);

                // Add missing fields adds one mapping row per field and focuses the first. The declared policyVersion
                // keeps its spelling, and a field name that is no plain identifier leaves the variable empty.
                var addMissing = catchBlock.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add missing fields", Exact = true });
                await Assertions.Expect(addMissing).ToBeVisibleAsync();
                await addMissing.ClickAsync();
                var variables = inspector.Locator("[data-signal-mapping]");
                await Assertions.Expect(variables).ToHaveCountAsync(3);
                await Assertions.Expect(inspector.Locator("[data-signal-mapping='0']")).ToBeFocusedAsync();
                await Assertions.Expect(variables.Nth(0)).ToHaveValueAsync("policyVersion");
                await Assertions.Expect(variables.Nth(1)).ToHaveValueAsync("reason");
                await Assertions.Expect(variables.Nth(2)).ToHaveValueAsync(string.Empty);
                var paths = Labeled("Payload path");
                await Assertions.Expect(paths.Nth(0)).ToHaveValueAsync("policyVersion");
                await Assertions.Expect(paths.Nth(1)).ToHaveValueAsync("reason");
                await Assertions.Expect(paths.Nth(2)).ToHaveValueAsync("effective-date");
                var types = Labeled("Data type");
                await Assertions.Expect(types.Nth(0)).ToHaveValueAsync("number");
                await Assertions.Expect(types.Nth(1)).ToHaveValueAsync("string");
                await Assertions.Expect(types.Nth(2)).ToHaveValueAsync("date");
                await Assertions.Expect(editor.Locator("#signal-catalog-2 [data-signal-catalog-add]")).ToBeHiddenAsync();
                await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "hosted-signal-catalog.png"),
                });

                // The click was one change: undo removes the three rows and keeps the name. Ctrl+Z inside the
                // focused mapping field would be the field's own undo, and hosted shortcuts need focus inside the
                // editor, so the keyboard undo follows a click on the empty canvas, which clears the selection.
                await editor.Locator("#svg").ClickAsync(new LocatorClickOptions { Position = new Position { X = 4, Y = 4 } });
                await page.Keyboard.PressAsync("Control+z");
                await Assertions.Expect(editor.Locator("#nodes .node[data-id='2']")).ToHaveClassAsync(new Regex("intermediateSignalCatchEvent"));
                await EditorInteractions.SelectNodeAsync(page, 2);
                await Assertions.Expect(variables).ToHaveCountAsync(0);
                await Assertions.Expect(Labeled("Signal name")).ToHaveValueAsync(policy);
                await Assertions.Expect(editor.Locator("#signal-catalog-2 [data-signal-catalog-add]")).ToBeVisibleAsync();
                await Assertions.Expect(editor.Locator("#nodes .node[data-id='2']")).ToHaveClassAsync(new Regex("intermediateSignalCatchEvent"));

                // End #4 becomes a signal end event; a thrower is offered only the entries that let workflows throw.
                // Search selects it; hosted shortcuts need focus inside the editor.
                await editor.Locator("#svg").ClickAsync(new LocatorClickOptions { Position = new Position { X = 4, Y = 4 } });
                await page.Keyboard.PressAsync("/");
                await Assertions.Expect(editor.Locator("#diagramSearchInput")).ToBeFocusedAsync();
                await page.Keyboard.TypeAsync("#4");
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Node #4", Exact = true }))
                    .ToHaveCountAsync(1);
                await EditorInteractions.SelectTypeAsync(page, "signalEndEvent");
                var endSuggestions = await SuggestionsAsync();
                await Assertions.Expect(endSuggestions.Locator($"option[value='{policy}']")).ToHaveCountAsync(1);
                await Assertions.Expect(endSuggestions.Locator($"option[value='{peopleOnly}']")).ToHaveCountAsync(0);
                await Assertions.Expect(endSuggestions.Locator($"option[value='{retired}']")).ToHaveCountAsync(0);

                // An archived name is noted, and saving shows the API's message and keeps the draft.
                var endBlock = editor.Locator("#signal-catalog-4");
                await Labeled("Signal name").FillAsync(retired);
                await Labeled("Signal name").PressAsync("Tab");
                await Assertions.Expect(endBlock.Locator(".signal-catalog-note")).ToHaveTextAsync("Archived in the signal catalog.");
                await Labeled("Scope").SelectOptionAsync("allWorkflows");
                await page.Locator("#save-workflow-version").ClickAsync();
                await Assertions.Expect(page.Locator("#editor-error"))
                    .ToContainTextAsync($"Signal end event #4 uses signal '{retired}', whose catalog entry is archived.");
                await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
                await Assertions.Expect(Labeled("Signal name")).ToHaveValueAsync(retired);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "hosted-signal-catalog-archived.png"),
                });

                // With the active entry, a payload that leaves out a required field is a publication problem only,
                // so the draft saves.
                await Labeled("Signal name").FillAsync(policy);
                await Labeled("Signal name").PressAsync("Tab");
                await Assertions.Expect(endBlock.Locator(".signal-catalog-title")).ToHaveTextAsync("Signal catalog entry");
                await Assertions.Expect(editor.Locator("#signal-catalog-4 [data-signal-catalog-add]")).ToBeVisibleAsync();
                await page.Locator("#save-workflow-version").ClickAsync();
                await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
                await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
                var savedId = long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
                var saved = await stack.SetupClient.GetWorkflowAsync(savedId);
                Assert.False(saved.IsPublished);
                var savedCatch = saved.Definition.FlowNodes.Single(node => node.Id == 2);
                Assert.Equal((BpmnFlowNodeTypes.IntermediateSignalCatchEvent, policy), (savedCatch.Type, savedCatch.Signal!.Name));
                Assert.Empty(savedCatch.Signal.OutputMappings ?? []);
                var savedEnd = saved.Definition.FlowNodes.Single(node => node.Id == 4);
                Assert.Equal((BpmnFlowNodeTypes.SignalEndEvent, policy, SignalThrowScopeKinds.AllWorkflows),
                    (savedEnd.Type, savedEnd.Signal!.Name, savedEnd.Signal.Scope!.Kind));
                Assert.Empty(savedEnd.Signal.Payload ?? []);

                // At phone width the hosted page stays within the viewport.
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForFunctionAsync("document.querySelector('#primary-navigation').getBoundingClientRect().right <= 0");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"),
                    "The hosted editor overflows horizontally at 390px.");
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, "hosted-signal-catalog-narrow.png"),
                });
                await page.SetViewportSizeAsync(1440, 900);
                await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            }
            finally
            {
                await stack.SetupClient.ArchiveSignalCatalogEntryIfUnusedAsync(policy);
                await stack.SetupClient.ArchiveSignalCatalogEntryIfUnusedAsync(peopleOnly);
            }
        });
    }

    /// <summary>The API's preview requests so far, from its request log.</summary>
    private int PreviewRequestCount() =>
        stack.ApiOutputLines.Count(line => line.Contains("HTTP POST /api/signal-catalog/preview ", StringComparison.Ordinal));

    /// <summary>Types into a field key by key, as a person does, and waits until the field holds the text.</summary>
    private static async Task TypeIntoAsync(IPage page, ILocator field, string text)
    {
        await field.ClickAsync();
        await page.Keyboard.TypeAsync(text, new KeyboardTypeOptions { Delay = 15 });
        await Assertions.Expect(field).ToHaveValueAsync(text);
    }

    /// <summary>
    /// Types a signal name and leaves the field, which sends it. The circuit may not handle the first change yet,
    /// so the name is typed again until the catalog panel shows <paramref name="shown"/>.
    /// </summary>
    private static Task TypeSignalNameAsync(IPage page, ILocator name, string signal, ILocator shown) =>
        RuntimeSupport.ClickUntilAsync(
            async () =>
            {
                await name.ClickAsync();
                await name.PressAsync("Control+a");
                await page.Keyboard.TypeAsync(signal, new KeyboardTypeOptions { Delay = 10 });
                await name.PressAsync("Tab");
            },
            async () => await shown.CountAsync() == 1 && await name.InputValueAsync() == signal,
            TimeSpan.FromSeconds(20));

    private static async Task AssertNoHorizontalOverflowAsync(IPage page, int width) =>
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"),
            $"The page overflows horizontally at {width}px.");
}
