using System.Text.Json;
using Flowbit.BrowserTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>Development authoring capability and standalone release guard on the real shared editor.</summary>
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
    public async Task DevelopmentControlsPreserveContractsAndStandaloneStaysClosed(int width, int height)
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
            var help = page.GetByRole(AriaRole.Button, new() { Name = "Help for Automatic multi-instance", Exact = true });
            await help.ClickAsync();
            await Assertions.Expect(page.GetByText("Development preview: these controls validate the shared contract.", new() { Exact = false })).ToBeVisibleAsync();
            await help.ClickAsync();
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
            await Assertions.Expect(page.Locator("#inspector .expression-validation").Last).ToContainTextAsync("maxConcurrency=1");
            await page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true }).FillAsync("1");
            await Assertions.Expect(page.Locator("#inspector .expression-validation").Last).ToHaveTextAsync("");
            await page.GetByLabel("Execution mode", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"hosted-{width}.png") });
            await page.GetByRole(AriaRole.Button, new() { Name = "+ Add timer boundary event", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(4);
            await page.Locator("#inspectorPin").ClickAsync();
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await page.Mouse.MoveAsync(20, height - 100);
            await page.Locator("#wfName").FocusAsync();
            await page.WaitForFunctionAsync("!document.querySelector('.editor-workspace').classList.contains('inspector-revealed')");
            await page.Locator("#fitViewBtn").ClickAsync();
            await EditorInteractions.SelectNodeAsync(page, 2);
            await Assertions.Expect(page.GetByLabel("Maximum concurrency (optional)", new() { Exact = true })).ToHaveValueAsync("1");
            await page.GotoAsync(address + "/?mode=standalone");
            await page.WaitForFunctionAsync("window.editorReady === true");
            await EditorInteractions.SelectNodeAsync(page, 2);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Automatic multi-instance" })).ToHaveCountAsync(0);
            Assert.True(await page.EvaluateAsync<bool>("() => { try { window.editor.prepareExport(); return false; } catch { return true; } }"));
            await Assertions.Expect(page.Locator("#validation-errors")).ToContainTextAsync("Flow node #2 automatic multiInstance is not enabled.");
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
                scope = "Shared editor public Development mount and standalone; no API, Worker or adapter execution."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string Html(string mode) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <link rel="icon" href="data:,"><link rel="stylesheet" href="/flowbit-editor.css">
        <style>body{margin:0}#editor{height:100vh;width:100vw}</style></head><body><div id="editor"></div>
        <script src="/editor-template.js"></script><script src="/flowbit-editor.js"></script><script>
        fetch('/workflow').then(response=>response.json()).then(model=>{
          window.editor=FlowbitEditor.mount(document.querySelector('#editor'),{mode:'{{mode}}',automaticMultiInstanceEnabled:true});
          window.editor.load(model);window.editor.setHostState({loaded:true,saving:false,canSave:true,version:'Development fixture'});window.editorReady=true;
        });</script></body></html>
        """;

    private const string Definition = """
        {"id":"automatic-items","name":"Automatic items","initialEventId":1,"lanes":[],
         "variables":[{"id":1,"name":"items","dataType":"number","isArray":true,"defaultValue":[2,2,3]},
                      {"id":2,"name":"results","dataType":"json","isArray":false,"defaultValue":[]}],
         "flowNodes":[{"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
          {"id":2,"type":"serviceTask","name":"Notify","x":250,"y":180,"asyncBefore":true,
           "service":{"type":"rest","method":"POST","url":"https://example.test/notify","timeoutSeconds":30,"headers":[],"outputMappings":[]},
           "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"items","completionEvaluation":"afterAll","maxConcurrency":4,"outputVariables":[],"resultVariable":"results"}},
          {"id":3,"type":"endEvent","name":"End","x":550,"y":200}],
         "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":2},{"id":201,"sourceRef":2,"targetRef":3}]}
        """;

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }
}
