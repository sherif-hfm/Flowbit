using System.Text.Json;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowEditorHostTimerScheduleTests
{
    [Fact]
    public async Task TimerSchedulesAskBlazorAndReportUnavailableInsteadOfFailing()
    {
        using var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("""
            let mounted = null;
            const document = { body: {}, createElement: () => ({ remove() {} }), head: { append: element => element.onload() } };
            const MutationObserver = class { observe() {} disconnect() {} };
            const window = { FlowbitEditor: { mount: (_root, options) => { mounted = options; return { dispose() {} }; } } };
            """);
        engine.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-editor-host.js"))
            .Replace("export async function create", "async function create", StringComparison.Ordinal)
            .Replace("export async function createViewer", "async function createViewer", StringComparison.Ordinal));
        var result = await engine.EvaluateAsync("""
            (async () => {
              const calls = [];
              let fail = false;
              const dotnet = {
                invokeMethodAsync: (method, ...args) => {
                  calls.push({ method, args });
                  return fail ? Promise.reject(new Error('Circuit lost.')) : Promise.resolve({ status: 'ok', method });
                }
              };
              const host = await create({ isConnected: true }, dotnet, { template: 'template', stylesheet: 'css', script: 'script' });
              const request = { cron: '0 8 * * 1-5', timeZone: 'Europe/London', fromUtc: null, count: 5 };
              const preview = await mounted.timerSchedules.preview(request);
              const zones = await mounted.timerSchedules.timeZones();
              fail = true;
              const failed = await mounted.timerSchedules.preview(request);
              host.dispose();
              const callsBeforeClosed = calls.length;
              const closed = await mounted.timerSchedules.preview(request);
              return JSON.stringify({ calls, preview, zones, failed, closed, callsBeforeClosed, callsAfterClosed: calls.length });
            })()
            """);
        using var json = JsonDocument.Parse(result.AsString());
        var root = json.RootElement;

        var calls = root.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal("PreviewTimerSchedule", calls[0].GetProperty("method").GetString());
        Assert.Equal(
            """[{"cron":"0 8 * * 1-5","timeZone":"Europe/London","fromUtc":null,"count":5}]""",
            calls[0].GetProperty("args").GetRawText());
        Assert.Equal("GetTimerScheduleTimeZones", calls[1].GetProperty("method").GetString());
        Assert.Equal("PreviewTimerSchedule", root.GetProperty("preview").GetProperty("method").GetString());
        Assert.Equal("GetTimerScheduleTimeZones", root.GetProperty("zones").GetProperty("method").GetString());
        Assert.Equal("""{"status":"unavailable"}""", root.GetProperty("failed").GetRawText());
        Assert.Equal("""{"status":"unavailable"}""", root.GetProperty("closed").GetRawText());
        Assert.Equal(root.GetProperty("callsBeforeClosed").GetInt32(), root.GetProperty("callsAfterClosed").GetInt32());
    }
}
