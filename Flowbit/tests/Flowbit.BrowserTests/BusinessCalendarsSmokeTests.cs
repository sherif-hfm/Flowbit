using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// The Business calendars page end to end: a settings administrator adds a calendar with a lunch break, a short Friday,
/// and a half-day holiday, previews a working day across the London daylight-saving change, edits it to add a closed
/// holiday, cannot delete it while a published workflow names it, and deletes it once that workflow is gone. Dates are
/// typed on the keyboard, including leading zeros over a date already entered.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class BusinessCalendarsSmokeTests(BrowserStackFixture stack)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R11_AdministratorsManageBusinessCalendars(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"r11-business-calendars-{width}", width, height);
        await scenario.RunAsync("business-calendars", async () =>
        {
            var name = $"browser-{width}-{Guid.NewGuid().ToString("N")[..6]}";
            long? workflowId = null;
            try
            {
                await RuntimeSupport.ApplyIdentityAsync(scenario, "calendar-admin", ["admin"]);
                var page = await scenario.OpenUiAsync("business-calendars");
                var seeded = page.Locator("tbody tr", new() { HasText = "examples.weekdays" });
                await Assertions.Expect(seeded).ToContainTextAsync("Etc/UTC");
                await Assertions.Expect(seeded).ToContainTextAsync("Mon–Fri 09:00–17:00");

                // Add a calendar: a lunch break Monday to Thursday, a short Friday, and a half-day holiday.
                await page.GetByRole(AriaRole.Button, new() { Name = "Add calendar", Exact = true }).First.ClickAsync();
                await Assertions.Expect(page.Locator("#business-calendar-time-zone")).ToHaveValueAsync("Etc/UTC");
                await page.Locator("#business-calendar-name").FillAsync(name);
                await page.Locator("#business-calendar-time-zone").FillAsync("Europe/London");
                await page.Locator("#business-calendar-description").FillAsync("London office");
                foreach (var day in new[] { "monday", "tuesday", "wednesday", "thursday" })
                    await page.Locator($"#business-calendar-day-{day}").FillAsync("09:00-12:30, 13:30-17:30");
                await page.Locator("#business-calendar-day-friday").FillAsync("09:00-13:00");
                await page.Locator("#business-calendar-add-holiday").ClickAsync();
                await RuntimeSupport.TypeDateAsync(page, "#business-calendar-holiday-date-0", new DateOnly(2026, 12, 24));
                await page.Locator("#business-calendar-holiday-name-0").FillAsync("Christmas Eve");
                await page.Locator("#business-calendar-holiday-hours-0").FillAsync("09:00-12:00");

                // Friday 10:00 plus one working day is Monday 10:00, after the clocks went forward on Sunday 29 March.
                // Dates are typed on the keyboard, as a person enters them.
                await RuntimeSupport.TypeDateTimeLocalAsync(page, "#business-calendar-preview-start", new DateTime(2026, 3, 27, 10, 0, 0));
                await page.Locator("#business-calendar-preview-duration").FillAsync("P1D");
                await page.Locator("#business-calendar-preview").ClickAsync();
                await Assertions.Expect(page.Locator("#business-calendar-preview-result"))
                    .ToHaveTextAsync("Due Mon 30 Mar 2026 10:00 (UTC+01:00), 2026-03-30 09:00 UTC.");

                // Leading zeros typed over that start keep every segment: Friday 2 January 09:05 plus one working day
                // is Monday 5 January 09:05, in winter time.
                await RuntimeSupport.TypeDateTimeLocalAsync(page, "#business-calendar-preview-start", new DateTime(2026, 1, 2, 9, 5, 0));
                await Assertions.Expect(page.Locator("#business-calendar-preview-start")).ToHaveValueAsync("2026-01-02T09:05");
                await page.Locator("#business-calendar-preview").ClickAsync();
                await Assertions.Expect(page.Locator("#business-calendar-preview-result"))
                    .ToHaveTextAsync("Due Mon 5 Jan 2026 09:05 (UTC+00:00), 2026-01-05 09:05 UTC.");

                // A week cannot be counted in working time.
                await page.Locator("#business-calendar-preview-duration").FillAsync("P1W");
                await page.Locator("#business-calendar-preview").ClickAsync();
                await Assertions.Expect(page.Locator("#business-calendar-preview-result .text-danger"))
                    .ToContainTextAsync("cannot be counted in a business calendar");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "business-calendar-form.png"), FullPage = true });

                await page.Locator("#business-calendar-save").ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"Business calendar {name} added.");
                var row = page.Locator("tbody tr", new() { HasText = name });
                await Assertions.Expect(row).ToContainTextAsync("Europe/London");
                await Assertions.Expect(row).ToContainTextAsync("London office");
                await Assertions.Expect(row).ToContainTextAsync("Mon–Thu 09:00–12:30, 13:30–17:30 · Fri 09:00–13:00");
                await Assertions.Expect(row).ToContainTextAsync("1 holiday");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "business-calendars.png"), FullPage = true });

                // Editing keeps the name and the holiday's own hours.
                await row.GetByRole(AriaRole.Button, new() { Name = $"Edit {name}", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#business-calendar-name")).ToHaveAttributeAsync("readonly", "");
                await Assertions.Expect(page.Locator("#business-calendar-day-monday")).ToHaveValueAsync("09:00-12:30, 13:30-17:30");
                await Assertions.Expect(page.Locator("#business-calendar-holiday-date-0")).ToHaveValueAsync("2026-12-24");
                await Assertions.Expect(page.Locator("#business-calendar-holiday-hours-0")).ToHaveValueAsync("09:00-12:00");

                // New Year's Day 2028 is a Saturday, so its holiday moves to Monday 3 January. The leading zeros typed
                // over the first date must not clear it.
                await page.Locator("#business-calendar-add-holiday").ClickAsync();
                await RuntimeSupport.TypeDateAsync(page, "#business-calendar-holiday-date-1", new DateOnly(2028, 1, 1));
                await page.Locator("#business-calendar-holiday-name-1").FillAsync("New Year's Day (substitute)");
                await RuntimeSupport.TypeDateAsync(page, "#business-calendar-holiday-date-1", new DateOnly(2028, 1, 3));
                await Assertions.Expect(page.Locator("#business-calendar-holiday-date-1")).ToHaveValueAsync("2028-01-03");
                await page.Locator("#business-calendar-description").FillAsync("London office, half-day Christmas Eve");
                await page.Locator("#business-calendar-save").ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"Business calendar {name} updated.");
                await Assertions.Expect(row).ToContainTextAsync("London office, half-day Christmas Eve");
                await Assertions.Expect(row).ToContainTextAsync("2 holidays");
                var saved = (await stack.SetupClient.ListBusinessCalendarsAsync()).Single(calendar => calendar.Name == name);
                Assert.Equal([new DateOnly(2026, 12, 24), new DateOnly(2028, 1, 3)], saved.Holidays.Select(holiday => holiday.Date));

                // A published workflow that names the calendar blocks its deletion.
                var workflow = await stack.SetupClient.CreateAndPublishAsync(CalendarWorkflow($"browser-calendar-{name}", name));
                workflowId = workflow.Id;
                var confirmation = $"Delete business calendar '{name}'? Workflows can no longer name it.";
                scenario.ExpectDialogOnce("confirm", confirmation, accept: true);
                await row.GetByRole(AriaRole.Button, new() { Name = $"Delete {name}", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".alert-danger"))
                    .ToContainTextAsync("cannot be deleted: 1 published workflow definition(s) use it");
                await Assertions.Expect(row).ToHaveCountAsync(1);

                // Once the workflow is gone the calendar can be deleted.
                await stack.SetupClient.DeleteWorkflowAsync(workflow.Id);
                workflowId = null;
                scenario.ExpectDialogOnce("confirm", confirmation, accept: true);
                await row.GetByRole(AriaRole.Button, new() { Name = $"Delete {name}", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"Business calendar {name} deleted.");
                await Assertions.Expect(row).ToHaveCountAsync(0);

                // Only settings administrators manage calendars.
                await RuntimeSupport.ApplyIdentityAsync(scenario, "calendar-viewer", ["viewer"]);
                await scenario.OpenUiAsync("business-calendars");
                await Assertions.Expect(page.Locator(".alert-danger")).ToContainTextAsync("does not have the configured settings administrator role");
                await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(0);
            }
            finally
            {
                if (workflowId is { } id)
                    await stack.SetupClient.DeleteWorkflowAsync(id);
                foreach (var leftover in (await stack.SetupClient.ListBusinessCalendarsAsync()).Where(calendar => calendar.Name == name))
                    await stack.SetupClient.DeleteBusinessCalendarAsync(leftover.Id, leftover.UpdatedAt);
            }
        });
    }

    private static WorkflowModel CalendarWorkflow(string workflowKey, string calendar) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Review",
                Type = BpmnFlowNodeTypes.UserTask,
                DueDateExpression = "P2D",
                DueDateCalendar = calendar
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
        ]
    };
}
