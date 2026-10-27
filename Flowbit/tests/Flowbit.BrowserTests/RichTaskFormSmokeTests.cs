using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R9: typed start and action forms. Covers controls chosen by data type,
/// labels, help text, fixed options, the date-time value sent with the
/// browser's offset in a fixed time zone, a rejected number entry, and the
/// exact stored values read back over HTTP, at desktop and phone widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class RichTaskFormSmokeTests(BrowserStackFixture stack)
{
    // The start page reads the definition through the workflow API, which
    // requires the default definition-administration role.
    private static readonly string[] RequesterRoles = ["admin", "Reviewer"];

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R9_TypedFormsSubmitTypedValuesWithLabelsHelpAndOptions(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r9-rich-forms-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight,
            timezoneId: "Asia/Tokyo");
        await scenario.RunAsync("rich-forms", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-rich-forms.json");
            var (client, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "requester", RequesterRoles);
            var page = scenario.Page;
            await scenario.OpenUiAsync($"workflows/{workflow.Id}/start");

            // Each value gets the control for its type, with its label, help, and hints.
            var title = page.Locator("#start-variable-requestTitle");
            await Assertions.Expect(page.Locator("label[for='start-variable-requestTitle']"))
                .ToContainTextAsync("Request title requestTitle · string");
            await Assertions.Expect(page.Locator("#start-variable-requestTitle-help"))
                .ToHaveTextAsync("Name the item you need.\nKeep it under 60 characters.");
            await Assertions.Expect(title).ToHaveAttributeAsync("aria-describedby", "start-variable-requestTitle-help");
            await Assertions.Expect(title).ToHaveAttributeAsync("aria-required", "true");
            await Assertions.Expect(page.Locator("#start-variable-amount")).ToHaveAttributeAsync("type", "number");
            await Assertions.Expect(page.Locator("#start-variable-urgent option"))
                .ToHaveTextAsync(["Choose a value", "True", "False"]);
            await Assertions.Expect(page.Locator("#start-variable-neededBy")).ToHaveAttributeAsync("type", "date");
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt")).ToHaveAttributeAsync("type", "datetime-local");
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt-preview"))
                .ToHaveTextAsync("Uses your browser's time zone.");
            await Assertions.Expect(page.Locator("#start-variable-category option"))
                .ToHaveTextAsync(["Choose a value", "Hardware", "Software", "services"]);
            await Assertions.Expect(page.Locator("#start-variable-category-default"))
                .ToHaveTextAsync("Leave blank to use the default.");
            await Assertions.Expect(page.Locator("textarea#start-variable-tags")).ToHaveCountAsync(1);

            // A number the browser cannot read is reported and blocks the start
            // instead of being omitted.
            var amount = page.Locator("#start-variable-amount");
            await amount.PressSequentiallyAsync("12-");
            await Assertions.Expect(page.Locator("#start-variable-amount-error"))
                .ToHaveTextAsync("Enter a number in plain decimal notation, for example 1250.50.");
            await Assertions.Expect(amount).ToHaveAttributeAsync("aria-invalid", "true");
            await title.FillAsync("Laptop for the new analyst");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Start instance" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger"))
                .ToHaveTextAsync("Variable 'amount' has an incomplete or invalid number value.");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"workflows/\d+/start$"));

            await amount.FillAsync("1250.50");
            await Assertions.Expect(page.Locator("#start-variable-amount-error")).ToHaveCountAsync(0);
            await page.Locator("#start-variable-urgent").SelectOptionAsync("true");
            await page.Locator("#start-variable-neededBy").FillAsync("2026-09-09");
            await page.Locator("#start-variable-kickoffAt").FillAsync("2026-09-10T10:30");
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt-preview"))
                .ToHaveTextAsync("Sent as 2026-09-10T10:30:00+09:00 (Asia/Tokyo).");
            await page.Locator("#start-variable-category").SelectOptionAsync("hardware");
            await page.Locator("#start-variable-tags").FillAsync("laptop, onboarding");
            await AssertNoHorizontalOverflowAsync(page, viewportWidth);
            await ScreenshotCenteredAsync(
                page.Locator("section.form-surface"),
                Path.Combine(scenario.ArtifactDirectory, "rich-start-form.png"));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Start instance" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"instances/\d+$"));
            var instanceId = long.Parse(Regex.Match(page.Url, @"instances/(\d+)$").Groups[1].Value);

            var started = await client.GetInstanceAsync(instanceId);
            AssertStored(started, "requestTitle", JsonValueKind.String, "Laptop for the new analyst");
            AssertStored(started, "amount", JsonValueKind.Number, "1250.50");
            AssertStored(started, "urgent", JsonValueKind.True, "true");
            AssertStored(started, "neededBy", JsonValueKind.String, "2026-09-09");
            AssertStored(started, "kickoffAt", JsonValueKind.String, "2026-09-10T10:30:00+09:00");
            AssertStored(started, "category", JsonValueKind.String, "hardware");
            AssertStored(started, "tags", JsonValueKind.Array, "[\"laptop\",\"onboarding\"]");

            // The task form offers the action's fixed options and help text.
            var taskId = await RuntimeSupport.SoleUserTaskIdAsync(client, instanceId);
            await scenario.OpenUiAsync($"user-tasks/{taskId}");
            var decision = page.Locator($"#task-{taskId}-flow-201-decision");
            await Assertions.Expect(decision.Locator("option")).ToHaveTextAsync(["Choose a value", "Approve", "Reject"]);
            await Assertions.Expect(page.Locator($"#task-{taskId}-flow-201-decision-help"))
                .ToHaveTextAsync("Choose the outcome you agreed with the requester.");
            await Assertions.Expect(page.Locator($"#task-{taskId}-flow-201-score option"))
                .ToHaveTextAsync(["Choose a value", "Poor", "Fair", "Excellent"]);
            await decision.SelectOptionAsync("rejected");
            await page.Locator($"#task-{taskId}-flow-201-score").SelectOptionAsync("2.5");
            await page.Locator($"#task-{taskId}-flow-201-followUpOn").FillAsync("2026-12-01T09:00");
            await Assertions.Expect(page.Locator($"#task-{taskId}-flow-201-followUpOn-preview"))
                .ToHaveTextAsync("Sent as 2026-12-01T09:00:00+09:00 (Asia/Tokyo).");
            await AssertNoHorizontalOverflowAsync(page, viewportWidth);
            await ScreenshotCenteredAsync(
                page.Locator("article.action-card"),
                Path.Combine(scenario.ArtifactDirectory, "rich-task-form.png"));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Take action" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($@"instances/{instanceId}$"));

            var completed = await client.GetInstanceAsync(instanceId);
            Assert.Equal("completed", completed.Status);
            AssertStored(completed, "decision", JsonValueKind.String, "rejected");
            AssertStored(completed, "score", JsonValueKind.Number, "2.5");
            AssertStored(completed, "followUpOn", JsonValueKind.String, "2026-12-01T09:00:00+09:00");
            Assert.DoesNotContain(scenario.Warnings, warning => warning.StartsWith("console.warn", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R9_PickerEdgeCasesAndInstanceDetailActions(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r9-picker-edges-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight,
            timezoneId: "Europe/Berlin");
        await scenario.RunAsync("picker-edges", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-rich-forms.json");
            var (client, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "requester", RequesterRoles);
            var page = scenario.Page;
            await scenario.OpenUiAsync($"workflows/{workflow.Id}/start");
            var title = page.Locator("#start-variable-requestTitle");
            var amount = page.Locator("#start-variable-amount");
            var startButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Start instance" });
            await title.FillAsync("Standing desk");
            await amount.FillAsync("1250.50");

            // The wheel scrolls the page instead of stepping a focused amount.
            await amount.FocusAsync();
            await amount.HoverAsync();
            await page.Mouse.WheelAsync(0, 240);
            await Assertions.Expect(amount).ToHaveValueAsync("1250.50");
            Assert.False(await amount.EvaluateAsync<bool>("element => element === document.activeElement"));

            // A date started from empty and left partly filled raises no input
            // event; leaving the field reports it, and it blocks the start.
            var neededBy = page.Locator("#start-variable-neededBy");
            await neededBy.FocusAsync();
            await page.Keyboard.TypeAsync("09");
            await title.FocusAsync();
            await Assertions.Expect(page.Locator("#start-variable-neededBy-error")).ToHaveTextAsync("Enter a complete date.");
            await startButton.ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger"))
                .ToHaveTextAsync("Variable 'neededBy' has an incomplete or invalid date value.");
            await neededBy.FillAsync(string.Empty);
            await Assertions.Expect(page.Locator("#start-variable-neededBy-error")).ToHaveCountAsync(0);

            // A date-time with its date but no time is reported the same way.
            var kickoff = page.Locator("#start-variable-kickoffAt");
            await kickoff.FocusAsync();
            await page.Keyboard.TypeAsync("10252026");
            await title.FocusAsync();
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt-error"))
                .ToHaveTextAsync("Enter a complete date and time.");

            // Daylight saving in Berlin: 02:30 on 29 March 2026 is skipped, and
            // 02:30 on 25 October 2026 occurs twice and uses its first occurrence.
            await kickoff.FillAsync("2026-03-29T02:30");
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt-error"))
                .ToHaveTextAsync("This time does not exist in Europe/Berlin because of a daylight-saving change.");
            await kickoff.FillAsync("2026-10-25T02:30");
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt-error")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#start-variable-kickoffAt-preview"))
                .ToHaveTextAsync("Sent as 2026-10-25T02:30:00+02:00 (Europe/Berlin).");
            await startButton.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"instances/\d+$"));
            var instanceId = long.Parse(Regex.Match(page.Url, @"instances/(\d+)$").Groups[1].Value);
            var started = await client.GetInstanceAsync(instanceId);
            AssertStored(started, "kickoffAt", JsonValueKind.String, "2026-10-25T02:30:00+02:00");
            Assert.DoesNotContain(started.Variables, variable => variable.VariableName == "neededBy");

            // The instance-detail action form uses the same typed controls.
            await scenario.OpenUiAsync($"instances/{instanceId}");
            var actions = page.Locator("#actions");
            var decision = page.Locator("#instance-flow-201-decision");
            await Assertions.Expect(page.Locator("label[for='instance-flow-201-decision']"))
                .ToContainTextAsync("Decision decision · string");
            await Assertions.Expect(decision).ToHaveAttributeAsync("aria-describedby", "instance-flow-201-decision-help");
            await Assertions.Expect(decision.Locator("option")).ToHaveTextAsync(["Choose a value", "Approve", "Reject"]);
            await decision.SelectOptionAsync("approved");
            await page.Locator("#instance-flow-201-score").SelectOptionAsync("5");
            await page.Locator("#instance-flow-201-followUpOn").FillAsync("2026-11-02T09:00");
            await Assertions.Expect(page.Locator("#instance-flow-201-followUpOn-preview"))
                .ToHaveTextAsync("Sent as 2026-11-02T09:00:00+01:00 (Europe/Berlin).");
            await AssertNoHorizontalOverflowAsync(page, viewportWidth);
            await ScreenshotCenteredAsync(actions, Path.Combine(scenario.ArtifactDirectory, "instance-detail-action.png"));
            await actions.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action" }).ClickAsync();
            await Assertions.Expect(actions).ToContainTextAsync("No user actions are available.");

            var completed = await client.GetInstanceAsync(instanceId);
            Assert.Equal("completed", completed.Status);
            AssertStored(completed, "decision", JsonValueKind.String, "approved");
            AssertStored(completed, "score", JsonValueKind.Number, "5");
            AssertStored(completed, "followUpOn", JsonValueKind.String, "2026-11-02T09:00:00+01:00");
            Assert.DoesNotContain(scenario.Warnings, warning => warning.StartsWith("console.warn", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R9_InterruptAndAdministrativeFormsUseTypedControls(int viewportWidth, int viewportHeight)
    {
        // A time zone west of UTC, so the date-time values carry negative offsets.
        await using var scenario = await stack.CreateScenario(
            $"r9-interrupt-admin-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight,
            timezoneId: "America/New_York");
        await scenario.RunAsync("interrupt-admin", async () =>
        {
            var review = await RuntimeSupport.PublishAsync(stack, "runtime-rich-forms-mi.json");
            var forms = await RuntimeSupport.PublishAsync(stack, "runtime-rich-forms.json");
            var (client, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "requester", RequesterRoles);
            var page = scenario.Page;

            // The multi-instance interrupt form uses the typed controls.
            var reviewInstanceId = (await client.StartInstanceAsync(review.Id)).Id;
            await scenario.OpenUiAsync($"instances/{reviewInstanceId}");
            var interrupts = page.Locator("section", new PageLocatorOptions
            {
                Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Interrupt actions" }),
            });
            var stopReason = page.Locator("#instance-interrupt-203-stopReason");
            await Assertions.Expect(page.Locator("label[for='instance-interrupt-203-stopReason']"))
                .ToContainTextAsync("Reason stopReason · string");
            await Assertions.Expect(page.Locator("#instance-interrupt-203-stopReason-help"))
                .ToHaveTextAsync("Tell the reviewers why the review stopped.");
            await Assertions.Expect(stopReason).ToHaveAttributeAsync("aria-required", "true");
            await Assertions.Expect(stopReason.Locator("option"))
                .ToHaveTextAsync(["Choose a value", "Duplicate request", "Withdrawn by requester"]);
            await Assertions.Expect(page.Locator("#instance-interrupt-203-budgetCap")).ToHaveAttributeAsync("type", "number");
            await stopReason.SelectOptionAsync("withdrawn");
            await page.Locator("#instance-interrupt-203-budgetCap").FillAsync("2500.75");
            await page.Locator("#instance-interrupt-203-resumeAt").FillAsync("2026-11-02T09:00");
            await Assertions.Expect(page.Locator("#instance-interrupt-203-resumeAt-preview"))
                .ToHaveTextAsync("Sent as 2026-11-02T09:00:00-05:00 (America/New_York).");
            await AssertNoHorizontalOverflowAsync(page, viewportWidth);
            await ScreenshotCenteredAsync(interrupts, Path.Combine(scenario.ArtifactDirectory, "instance-interrupt-form.png"));
            scenario.ExpectDialogOnce("confirm", "Take 'Stop reviews' and cancel every unfinished work item?", accept: true);
            await interrupts.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Interrupt and cancel remaining items" })
                .ClickAsync();
            await Assertions.Expect(interrupts).ToHaveCountAsync(0);

            var stopped = await client.GetInstanceAsync(reviewInstanceId);
            Assert.Equal("completed", stopped.Status);
            AssertStored(stopped, "stopReason", JsonValueKind.String, "withdrawn");
            AssertStored(stopped, "budgetCap", JsonValueKind.Number, "2500.75");
            AssertStored(stopped, "resumeAt", JsonValueKind.String, "2026-11-02T09:00:00-05:00");

            // The instance administrative override panel uses them too.
            var formsInstanceId = (await client.StartInstanceAsync(forms.Id, new Dictionary<string, JsonElement>
            {
                ["requestTitle"] = JsonSerializer.SerializeToElement("Docking station"),
                ["amount"] = JsonSerializer.SerializeToElement(180),
            })).Id;
            await scenario.OpenUiAsync($"instances/{formsInstanceId}");
            var panel = page.Locator("#administrative-actions");
            await RuntimeSupport.ClickUntilAsync(
                async () => await panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Decide" })
                    .First.ClickAsync(),
                async () => await panel.Locator(".admin-action-inputs").IsVisibleAsync(),
                TimeSpan.FromSeconds(30));
            var decision = panel.Locator("#instance-admin-input-0");
            await Assertions.Expect(panel.Locator("label[for='instance-admin-input-0']"))
                .ToContainTextAsync("Decision decision · string");
            await Assertions.Expect(panel.Locator("#instance-admin-input-0-help"))
                .ToHaveTextAsync("Choose the outcome you agreed with the requester.");
            await Assertions.Expect(decision.Locator("option")).ToHaveTextAsync(["Choose a value", "Approve", "Reject"]);
            await Assertions.Expect(panel.Locator("#instance-admin-input-1 option"))
                .ToHaveTextAsync(["Choose a value", "Poor", "Fair", "Excellent"]);
            await decision.SelectOptionAsync("approved");
            await panel.Locator("#instance-admin-input-1").SelectOptionAsync("1");
            await panel.Locator("#instance-admin-input-2").FillAsync("2026-10-30T16:45");
            await Assertions.Expect(panel.Locator("#instance-admin-input-2-preview"))
                .ToHaveTextAsync("Sent as 2026-10-30T16:45:00-04:00 (America/New_York).");
            await page.GetByLabel("Reason (optional)").FillAsync("Typed override");
            await AssertNoHorizontalOverflowAsync(page, viewportWidth);
            await ScreenshotCenteredAsync(
                panel.Locator(".admin-action-inputs"),
                Path.Combine(scenario.ArtifactDirectory, "instance-admin-override-form.png"));
            await RuntimeSupport.ClickUntilAsync(
                async () => await panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review action" })
                    .ClickAsync(),
                async () => await panel.Locator(".admin-action-inputs .alert-warning").IsVisibleAsync(),
                TimeSpan.FromSeconds(30));
            await RuntimeSupport.ClickUntilAsync(
                async () => await panel.GetByRole(AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = "Confirm administrative action" }).ClickAsync(),
                async () => await panel.GetByRole(AriaRole.Link,
                    new LocatorGetByRoleOptions { Name = "View audit batch" }).IsVisibleAsync(),
                TimeSpan.FromSeconds(30));
            var href = await panel.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "View audit batch" })
                .GetAttributeAsync("href");
            var batchId = long.Parse(Regex.Match(href ?? string.Empty, @"batchId=(\d+)").Groups[1].Value);

            var overridden = await client.GetInstanceAsync(formsInstanceId);
            Assert.Equal("completed", overridden.Status);
            AssertStored(overridden, "decision", JsonValueKind.String, "approved");
            AssertStored(overridden, "score", JsonValueKind.Number, "1");
            AssertStored(overridden, "followUpOn", JsonValueKind.String, "2026-10-30T16:45:00-04:00");

            // The audit batch keeps the action's declarations with their form fields.
            var batch = await client.GetAdministrativeActionBatchAsync(batchId);
            var stored = batch.Action.Variables.Single(variable => variable.Name == "decision");
            Assert.Equal("Decision", stored.Label);
            Assert.Equal("Choose the outcome you agreed with the requester.", stored.HelpText);
            Assert.Equal(
                [("approved", "Approve"), ("rejected", "Reject")],
                stored.Options!.Select(option => (option.Value!.Value.GetString(), option.Label)));
            Assert.Equal("approved", batch.CommonVariables["decision"].GetString());
            Assert.DoesNotContain(scenario.Warnings, warning => warning.StartsWith("console.warn", StringComparison.Ordinal));
        });
    }

    private static void AssertStored(InstanceDetailDto instance, string name, JsonValueKind kind, string expected)
    {
        var value = instance.Variables.Last(variable => variable.VariableName == name).Value;
        Assert.Equal(kind, value.ValueKind);
        Assert.Equal(expected, kind == JsonValueKind.String ? value.GetString() : value.GetRawText());
    }

    // An element scrolled to the top edge would sit under the app's sticky top bar;
    // centred, an element shorter than the viewport is captured uncovered.
    private static async Task ScreenshotCenteredAsync(ILocator element, string path)
    {
        await element.EvaluateAsync("element => element.scrollIntoView({ block: 'center' })");
        await element.ScreenshotAsync(new LocatorScreenshotOptions { Path = path });
    }

    private static async Task AssertNoHorizontalOverflowAsync(IPage page, int viewportWidth) =>
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
            $"The form overflows horizontally at {viewportWidth}px.");
}
