using System.Text.Json;
using Flowbit.BrowserTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>Automatic multi-instance authoring in the real shared editor, hosted and standalone.</summary>
public sealed class AutomaticMultiInstanceEditorBrowserTests : IAsyncLifetime
{
    private WebApplication? host;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private string address = "";
    private string artifacts = "";

    public async Task InitializeAsync()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
        artifacts = Path.Combine(repository!.FullName, "artifacts/browser/automatic-mi-editor", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(artifacts);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        host = builder.Build();
        foreach (var name in new[] { "flowbit-editor.js", "flowbit-editor.css", "editor-template.js" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures/editor", name);
            host.MapGet("/" + name, () => Results.File(path, name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/javascript"));
        }
        host.MapGet("/", (string mode) => Results.Content(Html(mode == "hosted" ? "hosted" : "standalone"), "text/html"));
        host.MapGet("/workflow", () => Results.Text(Definition, "application/json"));
        host.MapGet("/favicon.ico", () => Results.NoContent());
        await host.StartAsync();
        address = host.Urls.Single();
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task AutomaticControlsPreserveContractsInHostedAndStandaloneEditors(int width, int height)
    {
        var diagnostics = new List<string>();
        await using var context = await browser!.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15000);
        page.PageError += (_, error) => diagnostics.Add(error);
        page.Console += (_, message) => { if (message.Type is "error" or "warning") diagnostics.Add(message.Type + ": " + message.Text); };
        try
        {
            await page.GotoAsync(address + "/?mode=hosted");
            await page.WaitForFunctionAsync("window.editorReady === true");
            await EditorInteractions.SelectNodeAsync(page, 2);
            await EditorInteractions.PinInspectorOpenAsync(page);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Automatic multi-instance" })).ToBeVisibleAsync();
            // The help opens and closes; its wording belongs to the editor and is not pinned here.
            var help = page.GetByRole(AriaRole.Button, new() { Name = "Help for Automatic multi-instance", Exact = true });
            var helpPanel = page.Locator("#" + await help.GetAttributeAsync("aria-controls"));
            await help.ClickAsync();
            await Assertions.Expect(help).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(helpPanel).ToBeVisibleAsync();
            await Assertions.Expect(helpPanel).Not.ToHaveTextAsync("");
            await help.ClickAsync();
            await Assertions.Expect(helpPanel).ToBeHiddenAsync();
            await page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true }).FillAsync("2");
            await page.GetByRole(AriaRole.Button, new() { Name = "+ Add local output", Exact = true }).ClickAsync();
            await page.GetByLabel("Local name", new() { Exact = true }).FillAsync("receipt");
            await page.GetByLabel("Local nullable", new() { Exact = true }).CheckAsync();
            await page.GetByLabel("Local validation (NCalc, optional)", new() { Exact = true }).FillAsync("true");
            var exported = await page.EvaluateAsync<JsonElement>("window.editor.prepareExport()");
            Assert.False(exported.TryGetProperty("errors", out var errors), errors.ToString());
            using (var parsed = JsonDocument.Parse(exported.GetProperty("json").GetString()!))
            {
                var multi = parsed.RootElement.GetProperty("flowNodes")[1].GetProperty("multiInstance");
                Assert.Equal("afterAll", multi.GetProperty("completionEvaluation").GetString());
                Assert.Equal(2, multi.GetProperty("maxConcurrency").GetInt32());
                Assert.Equal("receipt", multi.GetProperty("outputVariables")[0].GetProperty("name").GetString());
            }
            await page.GetByLabel("Execution mode", new() { Exact = true }).SelectOptionAsync("sequential");
            await Assertions.Expect(BodyStatus(page)).Not.ToHaveTextAsync("");
            await page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true }).FillAsync("1");
            await Assertions.Expect(BodyStatus(page)).ToHaveTextAsync("");
            await page.GetByLabel("Execution mode", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"hosted-{width}.png") });
            await page.GetByRole(AriaRole.Button, new() { Name = "+ Add timer boundary event", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(6);
            await page.Locator("#inspectorPin").ClickAsync();
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await page.Mouse.MoveAsync(20, height - 100);
            await page.Locator("#wfName").FocusAsync();
            await page.WaitForFunctionAsync("!document.querySelector('.editor-workspace').classList.contains('inspector-revealed')");
            await page.Locator("#fitViewBtn").ClickAsync();
            await EditorInteractions.SelectNodeAsync(page, 2);
            await Assertions.Expect(page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true })).ToHaveValueAsync("1");

            // A script body: NCalc assignments write its declared item-local output.
            await EditorInteractions.PinInspectorOpenAsync(page);
            await SelectBySearchAsync(page, 4);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Automatic multi-instance" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByLabel("Execution mode", new() { Exact = true })).ToHaveValueAsync("parallel");
            await Assertions.Expect(page.GetByLabel("Collection variable", new() { Exact = true })).ToHaveValueAsync("items");
            await Assertions.Expect(page.GetByLabel("Local name", new() { Exact = true })).ToHaveValueAsync("doubled");
            await Assertions.Expect(page.Locator("#nodes .node[data-id='4'] .mi-marker title")).ToHaveTextAsync("Parallel multi-instance");
            await page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true }).FillAsync("3");
            await Assertions.Expect(BodyStatus(page)).ToHaveTextAsync("");
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"hosted-script-{width}.png") });

            // A call body: sequential admits one child at a time and accepts only concurrency 1.
            await SelectBySearchAsync(page, 5);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Automatic multi-instance" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByLabel("Aggregate result variable", new() { Exact = true })).ToHaveValueAsync("approvals");
            await page.GetByLabel("Execution mode", new() { Exact = true }).SelectOptionAsync("sequential");
            await Assertions.Expect(BodyStatus(page)).Not.ToHaveTextAsync("");
            await page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true }).FillAsync("1");
            await Assertions.Expect(BodyStatus(page)).ToHaveTextAsync("");
            await Assertions.Expect(page.Locator("#nodes .node[data-id='5'] .mi-marker title")).ToHaveTextAsync("Sequential multi-instance");
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"hosted-call-{width}.png") });
            var bodies = await page.EvaluateAsync<JsonElement>("window.editor.prepareExport()");
            Assert.False(bodies.TryGetProperty("errors", out var bodyErrors), bodyErrors.ToString());
            using (var parsed = JsonDocument.Parse(bodies.GetProperty("json").GetString()!))
            {
                var nodes = parsed.RootElement.GetProperty("flowNodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetInt32());
                var script = nodes[4].GetProperty("multiInstance");
                Assert.Equal(3, script.GetProperty("maxConcurrency").GetInt32());
                Assert.Equal("doubled", script.GetProperty("outputVariables")[0].GetProperty("name").GetString());
                Assert.Equal("ncalc", nodes[4].GetProperty("scriptFormat").GetString());
                var call = nodes[5].GetProperty("multiInstance");
                Assert.Equal(("sequential", 1), (call.GetProperty("mode").GetString(), call.GetProperty("maxConcurrency").GetInt32()));
                Assert.Equal("mi.item", nodes[5].GetProperty("callActivity").GetProperty("inputMappings")[0].GetProperty("sourceVariable").GetString());
            }

            // Undo walks the call body back to parallel, and redo restores it.
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(page.Locator("#nodes .node[data-id='5'] .mi-marker title")).ToHaveTextAsync("Parallel multi-instance");
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#redoBtn").ClickAsync();
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#redoBtn").ClickAsync();
            await Assertions.Expect(page.Locator("#nodes .node[data-id='5'] .mi-marker title")).ToHaveTextAsync("Sequential multi-instance");

            await page.GotoAsync(address + "/?mode=standalone");
            await page.WaitForFunctionAsync("window.editorReady === true");
            await SelectBySearchAsync(page, 2);
            // The standalone editor offers the same controls and saves the same bodies.
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Automatic multi-instance" })).ToHaveCountAsync(1);
            Assert.False(await page.EvaluateAsync<bool>("() => { try { window.editor.prepareExport(); return false; } catch { return true; } }"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"standalone-{width}.png") });
            Assert.Empty(diagnostics);
        }
        catch
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"failure-{width}.png") });
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(artifacts, $"evidence-{width}.json"), JsonSerializer.Serialize(new
            {
                browser = browser.Version, address, viewport = new { width, height }, diagnostics,
                scope = "Shared editor hosted and standalone mounts; no API, Worker or adapter execution."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // The body section's live status sits right after its "+ Add local output" button.
    private static ILocator BodyStatus(IPage page) =>
        page.Locator("#inspector button:text-is('+ Add local output') + .expression-validation");

    // Search selects any node at any zoom; a click on a small shape can start connect mode.
    private static async Task SelectBySearchAsync(IPage page, int nodeId)
    {
        // As the editor's own "/" shortcut does: the tools handle reveals the docked search,
        // then the box takes focus. The handle can sit under a pinned inspector at phone width.
        await page.Locator("#diagramToolsHandle").FocusAsync();
        await page.Locator("#diagramSearchInput").FocusAsync();
        await Assertions.Expect(page.Locator("#diagramSearchInput")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Control+a");
        await page.Keyboard.TypeAsync($"#{nodeId}");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#inspector").GetByRole(AriaRole.Heading, new() { Name = $"Node #{nodeId}", Exact = true }))
            .ToHaveCountAsync(1);
    }

    private static string Html(string mode) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <link rel="icon" href="data:,"><link rel="stylesheet" href="/flowbit-editor.css">
        <style>body{margin:0}#editor{height:100vh;width:100vw}</style></head><body><div id="editor"></div>
        <script src="/editor-template.js"></script><script src="/flowbit-editor.js"></script><script>
        fetch('/workflow').then(response=>response.json()).then(model=>{
          window.editor=FlowbitEditor.mount(document.querySelector('#editor'),{mode:'{{mode}}'});
          window.editor.load(model);window.editor.setHostState({loaded:true,saving:false,canSave:true,version:'Development fixture'});window.editorReady=true;
        });</script></body></html>
        """;

    private const string Definition = """
        {"id":"automatic-items","name":"Automatic items","initialEventId":1,"lanes":[],
         "variables":[{"id":1,"name":"items","dataType":"number","isArray":true,"defaultValue":[2,2,3]},
                      {"id":2,"name":"results","dataType":"json","isArray":false,"defaultValue":[]},
                      {"id":3,"name":"doubledResults","dataType":"json","isArray":false,"defaultValue":[]},
                      {"id":4,"name":"approvals","dataType":"json","isArray":false,"defaultValue":[]}],
         "flowNodes":[{"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
          {"id":2,"type":"serviceTask","name":"Notify","x":250,"y":180,"asyncBefore":true,
           "service":{"type":"rest","method":"POST","url":"https://example.test/notify","timeoutSeconds":30,"headers":[],"outputMappings":[]},
           "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"items","completionEvaluation":"afterAll","maxConcurrency":4,"outputVariables":[],"resultVariable":"results"}},
          {"id":4,"type":"scriptTask","name":"Double","x":500,"y":180,"asyncBefore":true,"scriptFormat":"ncalc",
           "assignments":[{"variable":"doubled","expression":"[mi.item] * 2"}],
           "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"items","completionEvaluation":"afterAll","maxConcurrency":4,
             "outputVariables":[{"name":"doubled","dataType":"number","isArray":false,"nullable":false}],"resultVariable":"doubledResults"}},
          {"id":5,"type":"callActivity","name":"Approve each","x":750,"y":180,
           "callActivity":{"workflowKey":"approval-child","inputMappings":[{"sourceVariable":"mi.item","variable":"amount","dataType":"number","isArray":false,"required":true}],"outputMappings":[]},
           "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"items","completionEvaluation":"afterAll","maxConcurrency":4,"outputVariables":[],"resultVariable":"approvals"}},
          {"id":3,"type":"endEvent","name":"End","x":1000,"y":200}],
         "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":2},{"id":201,"sourceRef":2,"targetRef":4},
                          {"id":401,"sourceRef":4,"targetRef":5},{"id":501,"sourceRef":5,"targetRef":3}]}
        """;

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }
}
