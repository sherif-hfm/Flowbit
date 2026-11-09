using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Jint;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The editor's cron mirror and inspector. The save validator span must agree with the server's rules message for
/// message and must never read Intl; the inspector keeps imported cron settings visible until an explicit edit.
/// </summary>
public sealed class EditorCronScheduleTests
{
    [Fact]
    public void Mirror_AcceptsAndRejectsTheSharedFixturesWithTheServersMessages()
    {
        var engine = CreateValidatorEngine();
        foreach (var cron in CronScheduleFixtures.Accepted)
        {
            Assert.True(CronScheduleRules.TryParse(cron, out var fields, out _));
            engine.SetValue("cron", cron);
            using var parsed = JsonDocument.Parse(engine.Evaluate(
                """
                (() => {
                  const result = parseCronSchedule(cron);
                  if (result.problem) return JSON.stringify({ problem: result.problem });
                  const list = values => [...values].sort((a, b) => a - b);
                  const s = result.schedule;
                  return JSON.stringify({
                    minutes: list(s.minutes), hours: list(s.hours), daysOfMonth: list(s.daysOfMonth),
                    lastDayOfMonth: s.lastDayOfMonth, months: list(s.months), daysOfWeek: list(s.daysOfWeek),
                    restrictsBothDays: s.restrictsBothDays
                  });
                })()
                """).AsString());
            var root = parsed.RootElement;
            Assert.False(root.TryGetProperty("problem", out var problem), $"'{cron}': {problem}");
            Assert.Equal(Bits(fields.Minutes), Values(root, "minutes"));
            Assert.Equal(Bits(fields.Hours), Values(root, "hours"));
            Assert.Equal(Bits(fields.DaysOfMonth), Values(root, "daysOfMonth"));
            Assert.Equal(Bits(fields.Months), Values(root, "months"));
            Assert.Equal(Bits(fields.DaysOfWeek), Values(root, "daysOfWeek"));
            Assert.Equal(fields.LastDayOfMonth, root.GetProperty("lastDayOfMonth").GetBoolean());
            Assert.Equal(fields.RestrictsBothDays, root.GetProperty("restrictsBothDays").GetBoolean());
        }

        foreach (var (cron, expected) in CronScheduleFixtures.Rejected)
        {
            engine.SetValue("cron", cron);
            Assert.Equal(expected, engine.Evaluate("parseCronSchedule(cron).problem").AsString());
        }
    }

    [Fact]
    public void SaveValidation_UsesTheServersMessagesForEveryTimerStartItCanCheck()
    {
        foreach (var row in CronTimerDefinitionTests.InvalidTimerStarts())
        {
            var timer = (TimerDefinitionModel)row[0];
            var expected = (string)row[1];
            // Only the server resolves zones: the editor checks presence and length.
            if (expected.Contains("is not an IANA time zone", StringComparison.Ordinal)
                || expected.Contains("must be written", StringComparison.Ordinal))
            {
                Assert.Empty(Validate(CronTimerDefinitionTests.TimerStartModel(timer)));
                continue;
            }

            Assert.Contains(expected, Validate(CronTimerDefinitionTests.TimerStartModel(timer)));
        }

        Assert.Empty(Validate(CronTimerDefinitionTests.TimerStartModel(
            new TimerDefinitionModel { Cron = " 0 8 * * 1-5 ", TimeZone = " Europe/London " })));
        Assert.Empty(Validate(CronTimerDefinitionTests.TimerStartModel(
            new TimerDefinitionModel { Cron = "\u0085 0 8 * * 1-5\u0085", TimeZone = "\u0085Europe/London\u2028" })));
    }

    [Theory]
    [InlineData("\uFEFF", true)]
    [InlineData("\u0085", false)]
    public void SaveValidation_ReportsCronOnACatchWhenTheServerDoes(string cron, bool reported)
    {
        const string message = "Timer catch event #3 timer.cron is supported only on timer start events.";
        var model = TimerExpressionDefinitionTests.Model("2030-01-01T09:00:00Z", "PT1H");
        model.FlowNodes.Single(node => node.Id == 3).Timer!.Cron = cron;

        // The server normalizes the model it validates, so the editor reads it first.
        var errors = Validate(model);

        if (reported)
        {
            Assert.Contains(message, errors);
            var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));
            Assert.Equal(message, error.Message);
        }
        else
        {
            Assert.DoesNotContain(errors, error => error.Contains("timer.cron", StringComparison.Ordinal));
            TimerExpressionDefinitionTests.Validate(model);
        }
    }

    [Fact]
    public void SaveValidation_ReportsCronWhereItDoesNotBelongAndKeepsTheThreeWayMessage()
    {
        var model = TimerExpressionDefinitionTests.Model("2030-01-01T09:00:00Z", "PT1H");
        var catchTimer = model.FlowNodes.Single(node => node.Id == 3).Timer!;
        catchTimer.Cron = "0 8 * * *";
        var boundary = model.FlowNodes.Single(node => node.Id == 4);
        boundary.Timer = new TimerDefinitionModel { TimeZone = "Europe/London" };

        var errors = Validate(model);

        Assert.Contains("Timer catch event #3 timer.cron is supported only on timer start events.", errors);
        Assert.Contains("Timer boundary event #4 timer.timeZone is supported only on timer start events.", errors);
        Assert.Contains("Timer event #4 must define exactly one of timeDate, timeDuration, or timeCycle.", errors);
        Assert.DoesNotContain(errors, error => error.Contains("or cron.", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveValidation_ReportsNonTextCronSettings()
    {
        var errors = ValidateJson(
            """
            {"id":"cron-types","name":"Cron types","variables":[],"lanes":[],
             "flowNodes":[{"id":1,"name":"Scheduled","type":"timerStartEvent","timer":{"cron":5,"timeZone":["Etc/UTC"]}},
                          {"id":2,"name":"Done","type":"endEvent"}],
             "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":2}]}
            """);

        Assert.Contains("Timer start event #1 timer.cron must be a string.", errors);
        Assert.Contains("Timer start event #1 timer.timeZone must be a string.", errors);
        Assert.Contains("Timer event #1 must define exactly one of timeDate, timeDuration, timeCycle, or cron.", errors);
    }

    [Fact]
    public void SaveValidation_NeverReadsIntl()
    {
        var model = TimerExpressionDefinitionTests.Model("2030-01-01T09:00:00Z", "PT1H");
        model.FlowNodes.Single(node => node.Id == 3).Timer!.Cron = "0 8 * * *";
        var json = JsonSerializer.Serialize(new
        {
            plain = model,
            cron = CronTimerDefinitionTests.TimerStartModel(new TimerDefinitionModel { Cron = "0 8 * * 7", TimeZone = "Mars/Olympus" })
        });

        var engine = CreateValidatorEngine();
        engine.SetValue("candidatesJson", json);
        var normal = engine.Evaluate(
            """
            (() => {
              const candidates = JSON.parse(candidatesJson);
              return JSON.stringify([validateModelForSave(candidates.plain), validateModelForSave(candidates.cron)]);
            })()
            """).AsString();
        var withoutIntl = engine.Evaluate(
            """
            (() => {
              globalThis.Intl = new Proxy({}, { get() { throw new Error('The save validator read Intl.'); } });
              const candidates = JSON.parse(candidatesJson);
              return JSON.stringify([validateModelForSave(candidates.plain), validateModelForSave(candidates.cron)]);
            })()
            """).AsString();

        Assert.Equal(normal, withoutIntl);
        Assert.Contains("day-of-week value 7 is not allowed", normal);
    }

    [Fact]
    public void Inspector_EditsValidatesAndExportsACronScheduleAndRepairsMisplacedSettings()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'cron-editor',
                name: 'Cron editor',
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Weekday start', type: 'timerStartEvent',
                    timer: { cron: '  0 8 * * 1-5  ', timeZone: ' Europe/London ' } },
                  { id: 2, name: 'Wait', type: 'intermediateTimerCatchEvent',
                    timer: { timeDuration: 'PT1H', cron: '0 8 * * *', timeZone: 'Etc/UTC' } },
                  { id: 3, name: 'Every two days', type: 'timerStartEvent', timer: { timeCycle: 'R/P2D' } },
                  { id: 4, name: 'Done', type: 'endEvent' }
                ],
                sequenceFlows: [
                  { id: 101, sourceRef: 1, targetRef: 2 },
                  { id: 201, sourceRef: 2, targetRef: 4 },
                  { id: 301, sourceRef: 3, targetRef: 4 }
                ]
              });
              const fieldFor = label => inspector.children.find(child =>
                String(child.className) === 'field' &&
                child.children.some(item => item.tagName === 'LABEL' && item.textContent === label));
              const child = (field, tag, index = 0) => field && field.children.filter(item => item.tagName === tag)[index];
              const selectFor = label => child(inspector.children.find(item =>
                String(item.className) === 'field' && item.innerHTML === '<label>' + label + '</label>'), 'SELECT');
              const chosen = select => /value="(\w+)" selected/.exec(select.innerHTML)[1];
              const options = select => [...select.innerHTML.matchAll(/value="(\w+)"/g)].map(match => match[1]);
              const notes = () => inspector.children.filter(item => String(item.className) === 'empty-note' && !item.hidden)
                .map(item => item.textContent);
              const show = nodeId => {
                selected = { kind: 'node', nodeId };
                selectedNodeIds.clear();
                selectedNodeIds.add(nodeId);
                inspector.replaceChildren();
                renderInspector();
              };
              const start = getNode(1);
              const loaded = JSON.stringify(start.timer);
              const before = JSON.stringify(model);
              show(1);
              const expression = child(fieldFor('Cron expression'), 'INPUT');
              const expressionStatus = child(fieldFor('Cron expression'), 'P', 0);
              const daysNote = child(fieldFor('Cron expression'), 'P', 1);
              const zone = child(fieldFor('Time zone'), 'INPUT');
              const zoneStatus = child(fieldFor('Time zone'), 'P', 0);
              const rendered = {
                schedule: chosen(selectFor('Schedule')),
                scheduleOptions: options(selectFor('Schedule')),
                expression: expression.value,
                expressionId: expression.id,
                zone: zone.value,
                status: expressionStatus.textContent,
                daysNoteHidden: daysNote.hidden,
                notes: notes(),
                unchanged: JSON.stringify(model) === before
              };

              expression.value = '0 8 13 * 5';
              expression.oninput();
              const bothDays = { hidden: daysNote.hidden, text: daysNote.textContent };
              expression.value = '0 8 * * 7';
              expression.oninput();
              const invalid = {
                status: expressionStatus.textContent,
                aria: expression.getAttribute('aria-invalid'),
                errors: prepareWorkflowExport().errors.filter(error => error.startsWith('Timer start event #1'))
              };
              expression.value = '0 17 L * *';
              expression.oninput();
              zone.value = '  ';
              zone.oninput();
              const missingZone = { status: zoneStatus.textContent, aria: zone.getAttribute('aria-invalid') };
              zone.value = 'Asia/Tokyo';
              zone.oninput();
              // The catch's misplaced settings still block the export.
              const blockedByCatch = prepareWorkflowExport().errors;

              // A catch keeps its imported cron settings visible until the explicit removal.
              show(2);
              const misplacedNotes = notes();
              const removeButton = inspector.children.find(item => item.tagName === 'BUTTON' &&
                item.textContent === 'Remove these settings');
              const catchBefore = JSON.stringify(getNode(2).timer);
              show(2);
              const catchAfterRedraw = JSON.stringify(getNode(2).timer);
              removeButton.onclick();
              const catchAfterRemoval = JSON.stringify(getNode(2).timer);
              const exported = JSON.parse(prepareWorkflowExport().json);

              // Choosing a schedule replaces the whole timer in one change that undo restores.
              const cycleStart = getNode(3);
              show(3);
              commitHistory();
              const cycleBefore = JSON.stringify(cycleStart.timer);
              const scheduleSelect = selectFor('Schedule');
              scheduleSelect.value = 'cron';
              scheduleSelect.onchange();
              commitHistory();
              const switched = JSON.stringify(getNode(3).timer);
              undo();
              const undone = JSON.stringify(getNode(3).timer);
              redo();
              const redone = JSON.stringify(getNode(3).timer);

              return JSON.stringify({
                loaded, rendered, bothDays, invalid, missingZone, blockedByCatch,
                exportedStart: exported.flowNodes.find(node => node.id === 1).timer,
                exportedCycleKeys: Object.keys(exported.flowNodes.find(node => node.id === 3).timer),
                misplacedNotes, hasRemoveButton: !!removeButton, catchBefore, catchAfterRedraw, catchAfterRemoval,
                cycleBefore, switched, undone, redone
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("""{"timeDate":null,"timeDuration":null,"timeCycle":null,"cron":"0 8 * * 1-5","timeZone":"Europe/London"}""",
            root.GetProperty("loaded").GetString());
        var rendered = root.GetProperty("rendered");
        Assert.Equal("cron", rendered.GetProperty("schedule").GetString());
        Assert.Equal(["timeDate", "timeDuration", "timeCycle", "cron"],
            rendered.GetProperty("scheduleOptions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("0 8 * * 1-5", rendered.GetProperty("expression").GetString());
        Assert.Equal("timer-cron-1", rendered.GetProperty("expressionId").GetString());
        Assert.Equal("Europe/London", rendered.GetProperty("zone").GetString());
        Assert.Equal(string.Empty, rendered.GetProperty("status").GetString());
        Assert.True(rendered.GetProperty("daysNoteHidden").GetBoolean());
        Assert.Contains(rendered.GetProperty("notes").EnumerateArray(), note =>
            note.GetString()!.StartsWith("This standalone editor checks the expression but cannot preview", StringComparison.Ordinal));
        Assert.True(rendered.GetProperty("unchanged").GetBoolean());

        Assert.False(root.GetProperty("bothDays").GetProperty("hidden").GetBoolean());
        Assert.Contains("must match both", root.GetProperty("bothDays").GetProperty("text").GetString());

        var invalid = root.GetProperty("invalid");
        Assert.Equal("timer.cron day-of-week value 7 is not allowed; write Sunday as 0.", invalid.GetProperty("status").GetString());
        Assert.Equal("true", invalid.GetProperty("aria").GetString());
        Assert.Equal(["Timer start event #1 timer.cron day-of-week value 7 is not allowed; write Sunday as 0."],
            invalid.GetProperty("errors").EnumerateArray().Select(item => item.GetString()));

        Assert.Equal(
            "timer.timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC.",
            root.GetProperty("missingZone").GetProperty("status").GetString());
        Assert.Equal("true", root.GetProperty("missingZone").GetProperty("aria").GetString());
        Assert.Equal(["Timer catch event #2 timer.cron is supported only on timer start events."],
            root.GetProperty("blockedByCatch").EnumerateArray().Select(item => item.GetString()));

        Assert.Equal(
            """{"timeDate":null,"timeDuration":null,"timeCycle":null,"cron":"0 17 L * *","timeZone":"Asia/Tokyo"}""",
            root.GetProperty("exportedStart").GetRawText());
        // A timer without cron exports exactly as before.
        Assert.Equal(["timeDate", "timeDuration", "timeCycle"],
            root.GetProperty("exportedCycleKeys").EnumerateArray().Select(item => item.GetString()));

        Assert.Contains(root.GetProperty("misplacedNotes").EnumerateArray(), note =>
            note.GetString() == "This timer has settings its schedule cannot use: timer.cron, timer.timeZone. Cron schedules are supported only on timer start events.");
        Assert.True(root.GetProperty("hasRemoveButton").GetBoolean());
        Assert.Equal(root.GetProperty("catchBefore").GetString(), root.GetProperty("catchAfterRedraw").GetString());
        Assert.Contains("\"cron\"", root.GetProperty("catchBefore").GetString());
        Assert.Equal("""{"timeDate":null,"timeDuration":"PT1H","timeCycle":null}""", root.GetProperty("catchAfterRemoval").GetString());

        Assert.Equal("""{"timeDate":null,"timeDuration":null,"timeCycle":null,"cron":"0 8 * * 1-5"}""",
            root.GetProperty("switched").GetString());
        Assert.Equal(root.GetProperty("cycleBefore").GetString(), root.GetProperty("undone").GetString());
        Assert.Equal(root.GetProperty("switched").GetString(), root.GetProperty("redone").GetString());
    }

    [Fact]
    public async Task HostedPreview_ShowsRepliesOnlyForTheScheduleOnScreenAndNeverDirtiesTheDocument()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "hosted");
        var json = (await engine.EvaluateAsync(
            """
            (async () => {
              // The test DOM has no connection state; every element here is on screen.
              document.createElement = (create => tag => Object.assign(create(tag), { isConnected: true }))(document.createElement);
              const requests = [];
              options.timerSchedules = {
                preview: request => new Promise((resolve, reject) => requests.push({ request, resolve, reject })),
                timeZones: () => Promise.resolve({ status: 'ok', timeZones: ['Etc/UTC', 'Europe/London'] })
              };
              const definition = {
                id: 'cron-preview',
                name: 'Cron preview',
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Weekday start', type: 'timerStartEvent', timer: { cron: '0 8 * * 1-5', timeZone: 'Europe/London' } },
                  { id: 2, name: 'Done', type: 'endEvent' }
                ],
                sequenceFlows: [{ id: 101, sourceRef: 1, targetRef: 2 }]
              };
              loadFromObject(JSON.parse(JSON.stringify(definition)));
              resetHistory();
              const show = nodeId => {
                selected = { kind: 'node', nodeId };
                selectedNodeIds.clear();
                selectedNodeIds.add(nodeId);
                inspector.replaceChildren();
                renderInspector();
              };
              const preview = () => inspector.children.find(child => String(child.className) === 'cron-preview');
              const button = () => preview().children.find(child => child.tagName === 'BUTTON');
              const reference = () => preview().children[0].children.find(child => child.tagName === 'INPUT');
              const expression = () => inspector.children.find(child => String(child.className) === 'field' &&
                child.children.some(item => item.tagName === 'LABEL' && item.textContent === 'Cron expression'))
                .children.find(item => item.tagName === 'INPUT');
              const reply = (cron, dueAt, localTime) => ({
                status: 'ok',
                preview: { cron, timeZone: 'Europe/London', fromUtc: '2026-11-08T12:00:00+00:00',
                  occurrences: [{ dueAt, localTime }] }
              });
              const before = JSON.stringify(model);
              show(1);
              const idle = { status: cronPreview.status, disabled: button().disabled, hasStandaloneNote:
                inspector.children.some(child => String(child.className) === 'empty-note' &&
                  String(child.textContent).startsWith('This standalone editor')) };

              // A reply for the schedule on screen is shown in the zone and in UTC.
              reference().value = '2026-11-08T12:00:00Z';
              reference().oninput();
              let pending = button().onclick();
              // A second click while the first request is pending sends nothing.
              await button().onclick();
              const loading = {
                status: cronPreview.status, disabled: button().disabled,
                ariaDisabled: button().getAttribute('aria-disabled'), requests: requests.length, request: requests[0].request
              };
              requests[0].resolve(reply('0 8 * * 1-5', '2026-11-09T08:00:00+00:00', '2026-11-09T08:00:00+00:00'));
              await pending;
              const table = preview().children.find(child => String(child.className) === 'cron-preview-results').children[0];
              const shown = {
                status: cronPreview.status,
                caption: table.children[0].textContent,
                row: table.children[2].children[0].children.map(cell => cell.textContent)
              };

              // Editing the schedule makes an answer, or a pending one, stale.
              pending = button().onclick();
              expression().value = '0 9 * * 1-5';
              expression().oninput();
              const afterEdit = cronPreview.status;
              requests[1].resolve(reply('0 8 * * 1-5', '2026-11-09T08:00:00+00:00', '2026-11-09T08:00:00+00:00'));
              await pending;
              const staleBySchedule = cronPreview.status;

              // Selecting again, or replacing the document, drops a late reply and leaves the preview idle.
              pending = button().onclick();
              show(1);
              requests[2].resolve(reply('0 9 * * 1-5', '2026-11-09T09:00:00+00:00', '2026-11-09T09:00:00+00:00'));
              await pending;
              const staleBySelection = { status: cronPreview.status, disabled: button().disabled };
              pending = button().onclick();
              loadFromObject(JSON.parse(JSON.stringify(definition)));
              show(1);
              requests[3].resolve(reply('0 8 * * 1-5', '2026-11-09T08:00:00+00:00', '2026-11-09T08:00:00+00:00'));
              await pending;
              const staleByDocument = cronPreview.status;

              // Failures keep the schedule and say what happened.
              const outcomes = [];
              for (const answer of [
                { status: 'invalid', message: 'timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC.' },
                { status: 'forbidden' },
                { status: 'unavailable' },
                null
              ]) {
                pending = button().onclick();
                if (answer) requests[requests.length - 1].resolve(answer);
                else requests[requests.length - 1].reject(new Error('Circuit lost.'));
                await pending;
                outcomes.push({ status: cronPreview.status, message: cronPreview.message });
              }

              const after = JSON.stringify(model);
              return JSON.stringify({
                idle, loading, shown, afterEdit, staleBySchedule, staleBySelection, staleByDocument, outcomes,
                requests: requests.length, unchanged: before === after, history: undoHistory.length,
                timer: getNode(1).timer
              });
            })()
            """)).AsString();
        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;

        Assert.Equal("idle", root.GetProperty("idle").GetProperty("status").GetString());
        Assert.False(root.GetProperty("idle").GetProperty("disabled").GetBoolean());
        Assert.False(root.GetProperty("idle").GetProperty("hasStandaloneNote").GetBoolean());

        var loading = root.GetProperty("loading");
        Assert.Equal("loading", loading.GetProperty("status").GetString());
        // Disabling the focused button would drop keyboard focus, so it is only marked unavailable.
        Assert.False(loading.GetProperty("disabled").GetBoolean());
        Assert.Equal("true", loading.GetProperty("ariaDisabled").GetString());
        Assert.Equal(1, loading.GetProperty("requests").GetInt32());
        Assert.Equal(
            """{"cron":"0 8 * * 1-5","timeZone":"Europe/London","fromUtc":"2026-11-08T12:00:00Z","count":5}""",
            loading.GetProperty("request").GetRawText());

        var shown = root.GetProperty("shown");
        Assert.Equal("ok", shown.GetProperty("status").GetString());
        Assert.Equal("Next run after Sun 2026-11-08 12:00 UTC, in Europe/London", shown.GetProperty("caption").GetString());
        Assert.Equal(["Mon 2026-11-09 08:00 +00:00", "Mon 2026-11-09 08:00 UTC"],
            shown.GetProperty("row").EnumerateArray().Select(cell => cell.GetString()));

        Assert.Equal("idle", root.GetProperty("afterEdit").GetString());
        Assert.Equal("idle", root.GetProperty("staleBySchedule").GetString());
        Assert.Equal("idle", root.GetProperty("staleBySelection").GetProperty("status").GetString());
        Assert.False(root.GetProperty("staleBySelection").GetProperty("disabled").GetBoolean());
        Assert.Equal("idle", root.GetProperty("staleByDocument").GetString());

        var outcomes = root.GetProperty("outcomes").EnumerateArray()
            .Select(item => (item.GetProperty("status").GetString(), item.GetProperty("message").GetString()))
            .ToArray();
        Assert.Equal(("invalid", "timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC."), outcomes[0]);
        Assert.Equal("forbidden", outcomes[1].Item1);
        Assert.StartsWith("You no longer have permission", outcomes[1].Item2);
        Assert.Equal("unavailable", outcomes[2].Item1);
        Assert.Equal("unavailable", outcomes[3].Item1);
        Assert.Contains("Your schedule is unchanged", outcomes[3].Item2);

        Assert.Equal(8, root.GetProperty("requests").GetInt32());
        // The edit was replaced with the reloaded document, and no preview changed the model.
        Assert.True(root.GetProperty("unchanged").GetBoolean());
        Assert.Equal("0 8 * * 1-5", root.GetProperty("timer").GetProperty("cron").GetString());
    }

    [Fact]
    public async Task Inspector_ReportsACronOrZoneThatIsNotTextAndNeverPreviewsIt()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "hosted");
        var json = (await engine.EvaluateAsync(
            """
            (async () => {
              // The test DOM has no connection state; every element here is on screen.
              document.createElement = (create => tag => Object.assign(create(tag), { isConnected: true }))(document.createElement);
              const requests = [];
              options.timerSchedules = {
                preview: request => { requests.push(request); return Promise.resolve({ status: 'unavailable' }); },
                timeZones: () => Promise.resolve({ status: 'ok', timeZones: ['Etc/UTC'] })
              };
              loadFromObject({
                id: 'typed-cron',
                name: 'Typed cron',
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Listed zone', type: 'timerStartEvent', timer: { cron: '0 8 * * 1-5', timeZone: ['Etc/UTC'] } },
                  { id: 2, name: 'Numeric cron', type: 'timerStartEvent', timer: { cron: 5, timeZone: 'Etc/UTC' } },
                  { id: 3, name: 'Done', type: 'endEvent' }
                ],
                sequenceFlows: [{ id: 101, sourceRef: 1, targetRef: 3 }, { id: 201, sourceRef: 2, targetRef: 3 }]
              });
              const show = nodeId => {
                selected = { kind: 'node', nodeId };
                selectedNodeIds.clear();
                selectedNodeIds.add(nodeId);
                inspector.replaceChildren();
                renderInspector();
              };
              const field = label => inspector.children.find(child => String(child.className) === 'field' &&
                child.children.some(item => item.tagName === 'LABEL' && item.textContent === label));
              const input = label => field(label).children.find(item => item.tagName === 'INPUT');
              const status = label => field(label).children.find(item => item.tagName === 'P');
              const button = () => inspector.children.find(child => String(child.className) === 'cron-preview')
                .children.find(child => child.tagName === 'BUTTON');
              const read = label => ({
                value: input(label).value,
                status: status(label).textContent,
                aria: input(label).getAttribute('aria-invalid'),
                disabled: button().disabled
              });

              show(1);
              const listedZone = read('Time zone');
              await button().onclick();
              const typeErrors = prepareWorkflowExport().errors.filter(error => error.includes('must be a string'));
              input('Time zone').value = 'Etc/UTC';
              input('Time zone').oninput();
              const typedZone = { ...read('Time zone'), zone: getNode(1).timer.timeZone };

              show(2);
              const numericCron = read('Cron expression');
              await button().onclick();
              return JSON.stringify({ listedZone, typeErrors, typedZone, numericCron, requests: requests.length });
            })()
            """)).AsString();
        using var result = JsonDocument.Parse(json);
        var root = result.RootElement;

        // The imported list is shown as JSON with the save validator's words, not as the text it would convert to.
        var listedZone = root.GetProperty("listedZone");
        Assert.Equal("""["Etc/UTC"]""", listedZone.GetProperty("value").GetString());
        Assert.Equal("timer.timeZone must be a string.", listedZone.GetProperty("status").GetString());
        Assert.Equal("true", listedZone.GetProperty("aria").GetString());
        Assert.True(listedZone.GetProperty("disabled").GetBoolean());
        Assert.Equal(
            ["Timer start event #1 timer.timeZone must be a string.", "Timer start event #2 timer.cron must be a string."],
            root.GetProperty("typeErrors").EnumerateArray().Select(item => item.GetString()));

        var typedZone = root.GetProperty("typedZone");
        Assert.Equal(string.Empty, typedZone.GetProperty("status").GetString());
        Assert.Equal("false", typedZone.GetProperty("aria").GetString());
        Assert.False(typedZone.GetProperty("disabled").GetBoolean());
        Assert.Equal("Etc/UTC", typedZone.GetProperty("zone").GetString());

        var numericCron = root.GetProperty("numericCron");
        Assert.Equal("5", numericCron.GetProperty("value").GetString());
        Assert.Equal("timer.cron must be a string.", numericCron.GetProperty("status").GetString());
        Assert.Equal("true", numericCron.GetProperty("aria").GetString());
        Assert.True(numericCron.GetProperty("disabled").GetBoolean());
        Assert.Equal(0, root.GetProperty("requests").GetInt32());
    }

    [Fact]
    public void Import_KeepsATimerOnAnotherNodeTypeUntilAnExplicitEditAsTheServerRefusesIt()
    {
        const string definition = """
            {
              "id": "kept-timer",
              "name": "Kept timer",
              "initialEventId": 1,
              "variables": [],
              "lanes": [],
              "flowNodes": [
                { "id": 1, "name": "Weekday start", "type": "startEvent", "timer": { "cron": "0 8 * * 1-5", "timeZone": "Europe/London" } },
                { "id": 2, "name": "Done", "type": "endEvent" }
              ],
              "sequenceFlows": [{ "id": 101, "sourceRef": 1, "targetRef": 2 }]
            }
            """;
        const string refused = "Flow node #1 defines a timer but is not a timer event.";
        var serverModel = JsonSerializer.Deserialize<WorkflowModel>(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(refused, Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(serverModel)).Message);

        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        engine.SetValue("definition", definition);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const show = nodeId => {
                selected = { kind: 'node', nodeId };
                selectedNodeIds.clear();
                selectedNodeIds.add(nodeId);
                inspector.replaceChildren();
                renderInspector();
              };
              const notes = () => inspector.children.filter(item => String(item.className) === 'empty-note' && !item.hidden)
                .map(item => item.textContent);
              const button = text => inspector.children.find(item => item.tagName === 'BUTTON' && item.textContent === text);
              const errors = () => prepareWorkflowExport().errors.filter(error => error.startsWith('Flow node #1') ||
                error.startsWith('Timer'));

              loadFromObject(JSON.parse(definition));
              const loaded = JSON.stringify(getNode(1).timer);
              show(1);
              show(1);
              const shown = {
                notes: notes(), hasRemove: !!button('Remove this timer'), timer: JSON.stringify(getNode(1).timer), errors: errors()
              };

              // Becoming a timer start adopts the schedule; a timer event that becomes another type loses it.
              changeNodeTypeFromInspector(getNode(1), 'timerStartEvent');
              const adopted = { timer: JSON.stringify(getNode(1).timer), errors: errors() };
              changeNodeTypeFromInspector(getNode(1), 'startEvent');
              const dropped = getNode(1).timer === undefined;

              // The removal is one change that undo restores.
              loadFromObject(JSON.parse(definition));
              resetHistory();
              show(1);
              commitHistory();
              button('Remove this timer').onclick();
              commitHistory();
              const removed = { gone: getNode(1).timer === undefined, errors: errors() };
              undo();
              const restored = JSON.stringify(getNode(1).timer);
              return JSON.stringify({ loaded, shown, adopted, dropped, removed, restored });
            })()
            """).AsString());

        var root = result.RootElement;
        const string authored = """{"cron":"0 8 * * 1-5","timeZone":"Europe/London"}""";
        Assert.Equal(authored, root.GetProperty("loaded").GetString());
        var shown = root.GetProperty("shown");
        Assert.Contains(shown.GetProperty("notes").EnumerateArray(), note => note.GetString() ==
            "This imported node defines a timer, which only a timer event can use, so the workflow cannot be saved: "
            + authored + ". Change the node to a timer event to keep its schedule, or remove the timer.");
        Assert.True(shown.GetProperty("hasRemove").GetBoolean());
        Assert.Equal(authored, shown.GetProperty("timer").GetString());
        Assert.Equal([refused], shown.GetProperty("errors").EnumerateArray().Select(item => item.GetString()));

        Assert.Equal("""{"timeDate":null,"timeDuration":null,"timeCycle":null,"cron":"0 8 * * 1-5","timeZone":"Europe/London"}""",
            root.GetProperty("adopted").GetProperty("timer").GetString());
        Assert.Empty(root.GetProperty("adopted").GetProperty("errors").EnumerateArray());
        Assert.True(root.GetProperty("dropped").GetBoolean());

        Assert.True(root.GetProperty("removed").GetProperty("gone").GetBoolean());
        Assert.Empty(root.GetProperty("removed").GetProperty("errors").EnumerateArray());
        Assert.Equal(authored, root.GetProperty("restored").GetString());
    }

    [Fact]
    public void TypeChange_ClearsCronWhenATimerStartBecomesACatch()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'cron-type-change',
                name: 'Cron type change',
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Scheduled', type: 'timerStartEvent', timer: { cron: '0 8 * * *', timeZone: 'Etc/UTC' } },
                  { id: 2, name: 'Cycle', type: 'timerStartEvent', timer: { timeCycle: 'R/PT1H', timeZone: 'Etc/UTC' } },
                  { id: 3, name: 'Done', type: 'endEvent' }
                ],
                sequenceFlows: [
                  { id: 101, sourceRef: 1, targetRef: 3 },
                  { id: 201, sourceRef: 2, targetRef: 3 }
                ]
              });
              const cronStart = getNode(1);
              const cycleStart = getNode(2);
              const changedCron = changeNodeTypeFromInspector(cronStart, 'intermediateTimerCatchEvent');
              const changedCycle = changeNodeTypeFromInspector(cycleStart, 'timerBoundaryEvent');
              return JSON.stringify({
                changedCron, changedCycle,
                cronTimer: cronStart.timer,
                cycleTimer: cycleStart.timer
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("changedCron").GetBoolean());
        Assert.True(root.GetProperty("changedCycle").GetBoolean());
        Assert.Equal("""{"timeDate":null,"timeDuration":"PT1H","timeCycle":null}""", root.GetProperty("cronTimer").GetRawText());
        Assert.Equal("""{"timeDate":null,"timeDuration":null,"timeCycle":"R/PT1H"}""", root.GetProperty("cycleTimer").GetRawText());
    }

    [Fact]
    public void Normalization_KeepsCronSettingsOnlyWhenPresentAndKeepsMalformedValues()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        var json = engine.Evaluate(
            """
            JSON.stringify([
              normalizeTimerDefinition({ timeCycle: 'R/P2D' }),
              normalizeTimerDefinition({ cron: '  0\t8 1-2 * 1,2  ', timeZone: ' Europe/London ' }),
              normalizeTimerDefinition({ timeDuration: 'PT1H', cron: '   ', timeZone: '' }),
              normalizeTimerDefinition({ cron: 5, timeZone: ['Etc/UTC'] })
            ])
            """).AsString();

        Assert.Equal(
            """[{"timeDate":null,"timeDuration":null,"timeCycle":"R/P2D"},"""
            + """{"timeDate":null,"timeDuration":null,"timeCycle":null,"cron":"0\t8 1-2 * 1,2","timeZone":"Europe/London"},"""
            + """{"timeDate":null,"timeDuration":"PT1H","timeCycle":null},"""
            + """{"timeDate":null,"timeDuration":null,"timeCycle":null,"cron":5,"timeZone":["Etc/UTC"]}]""",
            json);
    }

    [Fact]
    public void Normalization_TrimsCronAndItsZoneAsTheServerDoes()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        foreach (var value in new[] { "\u0085 0 8 * * *\uFEFF", "\uFEFFEtc/UTC\u2028", "\u0085", "\u3000", "\uFEFF" })
        {
            var model = CronTimerDefinitionTests.TimerStartModel(new TimerDefinitionModel { Cron = value, TimeZone = value });
            WorkflowModelMigrator.Normalize(model);
            var server = model.FlowNodes[0].Timer!;

            engine.SetValue("value", value);
            using var editor = JsonDocument.Parse(engine.Evaluate(
                "JSON.stringify(normalizeTimerDefinition({ cron: value, timeZone: value }))").AsString());

            Assert.Equal(server.Cron, editor.RootElement.TryGetProperty("cron", out var cron) ? cron.GetString() : null);
            Assert.Equal(server.TimeZone, editor.RootElement.TryGetProperty("timeZone", out var zone) ? zone.GetString() : null);
        }
    }

    private static IReadOnlyList<int> Values(JsonElement root, string name) =>
        root.GetProperty(name).EnumerateArray().Select(item => item.GetInt32()).ToList();

    private static IReadOnlyList<int> Bits(ulong bits) =>
        Enumerable.Range(0, 64).Where(index => (bits & (1UL << index)) != 0).ToList();

    private static IReadOnlyList<string> Validate(WorkflowModel model) =>
        ValidateJson(JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static IReadOnlyList<string> ValidateJson(string json)
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", json);
        return JsonSerializer.Deserialize<List<string>>(engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString()) ?? [];
    }

    private static Engine CreateValidatorEngine()
    {
        var html = EditorSource.Read();
        var match = Regex.Match(html, @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(match.Success, "The marked workflow save validator was not found.");
        var normalizeRoles = Regex.Match(html, @"function normalizeRoles\(roles\) \{[\s\S]*?(?=function normalizeAttributesForLoad)");
        Assert.True(normalizeRoles.Success, "The role-normalization helper was not found.");
        var engine = new Engine();
        engine.Execute(normalizeRoles.Value);
        engine.Execute(match.Groups["code"].Value);
        return engine;
    }
}
