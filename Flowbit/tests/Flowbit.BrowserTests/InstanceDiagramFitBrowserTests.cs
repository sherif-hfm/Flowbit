using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>Real SVG fit checks use only the public viewer API and a static localhost host; no runtime database.</summary>
public sealed class InstanceDiagramFitBrowserTests : IAsyncLifetime
{
    private WebApplication? host;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private string address = "";
    private string repository = "";
    private string artifacts = "";
    private readonly List<string> diagnostics = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task InitializeAsync()
    {
        repository = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
            .SelectMany(start => Ancestors(start))
            .First(path => File.Exists(Path.Combine(path, "examples/durable-jobs/01-async-before-after.json")));
        artifacts = Path.Combine(repository, "artifacts/browser/fit", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(artifacts);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        host = builder.Build();
        var assets = Path.Combine(AppContext.BaseDirectory, "Fixtures/editor");
        foreach (var name in new[] { "flowbit-editor.js", "flowbit-editor.css", "editor-template.js" })
        {
            var path = Path.Combine(assets, name);
            host.MapGet("/" + name, () => Results.File(path, name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/javascript"));
        }
        host.MapGet("/", (int width, string scenario) => Results.Content(Html(width, scenario), "text/html"));
        host.MapGet("/workflow", (string scenario) =>
        {
            var model = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "examples/durable-jobs/01-async-before-after.json")))!;
            foreach (var node in model["flowNodes"]!.AsArray())
            {
                if (scenario == "offset")
                {
                    node!["x"] = node["x"]!.GetValue<int>() + 4000;
                    node["y"] = node["y"]!.GetValue<int>() + 2600;
                }
                if (scenario == "captions" && node!["type"]!.GetValue<string>().EndsWith("Event", StringComparison.Ordinal))
                    node["name"] = "MMMMMMMMMMMMMMMMMM MMMMMMMMMMMMMMMMMM";
            }
            return Results.Json(model);
        });
        await host.StartAsync();
        address = host.Urls.Single();
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("FLOWBIT_BROWSER_HEADED") != "1" });
    }

    [Theory]
    [InlineData("canonical", 800, .62, .65, -179.249)]
    [InlineData("offset", 900, .70, .73, 2467.112)]
    [InlineData("captions", 900, .64, .67, -160.883)]
    public async Task RealSvgBoundsPreserveAbsoluteFitAcrossHistoryAndSemanticZoom(string scenario, int width, double minimumZoom, double maximumZoom, double expectedY)
    {
        await using var context = await browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1024, Height = 650 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(10000);
        page.PageError += (_, error) => diagnostics.Add(error);
        page.Console += (_, message) => { if (message.Type is "error" or "warning") diagnostics.Add(message.Type + ": " + message.Text); };
        page.RequestFailed += (_, request) => diagnostics.Add(request.Url + ": " + request.Failure);
        var observations = new List<object>();
        try
        {
            await page.GotoAsync($"{address}/?width={width}&scenario={scenario}");
            await page.WaitForFunctionAsync("window.viewerReady === true");
            var initial = await ReadAsync(page);
            observations.Add(new { stage = "initial", view = initial });
            Assert.InRange(initial.Zoom, minimumZoom, maximumZoom);
            Assert.InRange(initial.Y, expectedY - 2, expectedY + 2);
            Assert.All(initial.Nodes, node => Assert.True(node.FullyInside, $"Node {node.Id} is clipped on initial {scenario} fit."));
            if (scenario == "offset") Assert.True(initial.X > 3900 && initial.Y > 2300);
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, scenario + "-initial.png") });

            // Real Fit must measure labels even after the initial fit hid them.
            await page.Locator("#fitViewBtn").ClickAsync();
            SameView(initial, await ReadAsync(page));
            await page.GetByRole(AriaRole.Button, new() { Name = "Show history", Exact = true }).ClickAsync();
            SameView(initial, await ReadAsync(page));
            await Assertions.Expect(page.Locator(".instance-flow-count[data-traversal-count='999950'] text")).ToHaveTextAsync("×1m");
            await Assertions.Expect(page.Locator(".instance-flow-count[data-traversal-count='999950']"))
                .ToHaveAttributeAsync("aria-label", "Flow taken 999950 times");
            await page.Locator("#fitViewBtn").ClickAsync();
            SameView(initial, await ReadAsync(page));
            await page.GetByRole(AriaRole.Button, new() { Name = "Reload with history", Exact = true }).ClickAsync();
            SameView(initial, await ReadAsync(page));
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, scenario + "-history.png") });

            for (var click = 0; click < 4; click++) await page.Locator("#zoomOutBtn").ClickAsync();
            Assert.Equal("overview", await page.Locator("#svg").GetAttributeAsync("data-zoom-level"));
            await page.Locator("#fitViewBtn").ClickAsync();
            var restored = await ReadAsync(page);
            observations.Add(new { stage = "restored", view = restored });
            SameView(initial, restored);
            Assert.All(restored.Nodes, node => Assert.True(node.FullyInside));
            await page.GetByRole(AriaRole.Button, new() { Name = "Hide history", Exact = true }).ClickAsync();
            SameView(initial, await ReadAsync(page));
            await page.Locator("#fitViewBtn").ClickAsync();
            SameView(initial, await ReadAsync(page));
            Assert.Empty(diagnostics);
        }
        catch
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, scenario + "-failure.png") });
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(artifacts, "evidence.json"), JsonSerializer.Serialize(new
            {
                browser = browser.Version, headless = Environment.GetEnvironmentVariable("FLOWBIT_BROWSER_HEADED") != "1",
                address, scenario, viewport = new { width = 1024, height = 650 }, canvas = new { width, height = 448 }, diagnostics, observations
            }, Json));
        }
    }

    private static void SameView(View expected, View actual)
    {
        Assert.InRange(Math.Abs(actual.Zoom - expected.Zoom), 0, .0001);
        Assert.InRange(Math.Abs(actual.X - expected.X), 0, .001);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, .001);
    }

    private static async Task<View> ReadAsync(IPage page) => (await page.EvaluateAsync<JsonElement>("""
        () => {
          const svg=document.querySelector('#svg'), box=svg.viewBox.baseVal;
          const canvas=document.querySelector('.canvas-wrap').getBoundingClientRect();
          return { Zoom:canvas.width/box.width, X:box.x, Y:box.y,
            Nodes:[...document.querySelectorAll('#nodes .node')].map(node=>{
              const rect=node.getBoundingClientRect();
              return {Id:node.dataset.id,FullyInside:rect.left>=canvas.left&&rect.right<=canvas.right&&rect.top>=canvas.top&&rect.bottom<=canvas.bottom};
            })};
        }
        """)).Deserialize<View>()!;

    private static string Html(int width, string scenario) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/flowbit-editor.css">
        <style>body{margin:0;font-family:Arial,sans-serif}#viewer{width:{{width}}px;height:448px;border:1px solid #aaa}nav{padding:8px}</style></head><body>
        <nav><button onclick="setHistory(true)">Show history</button><button onclick="setHistory(false)">Hide history</button><button onclick="reloadHistory()">Reload with history</button></nav>
        <div id="viewer"></div><script src="/editor-template.js"></script><script src="/flowbit-editor.js"></script><script>
        fetch('/workflow?scenario={{scenario}}').then(r=>r.json()).then(model=>{
          const viewer=FlowbitEditor.mount(document.querySelector('#viewer'),{mode:'viewer'});
          const overlay=history=>({nodes:model.flowNodes.map(node=>({id:node.id,...({{(scenario == "offset" ? "false" : "node.id===4")}}?{state:'active',badge:'1'}:{}),...(history?{reached:true}:{})})),
            flows:history?model.sequenceFlows.map((flow,index)=>({id:flow.id,traversalCount:index===0?999950:1})):[]});
          window.setHistory=enabled=>viewer.setOverlay(overlay(enabled));
          window.reloadHistory=()=>viewer.load(model,overlay(true));
          viewer.load(model,overlay(false));window.viewerReady=true;
        });</script></body></html>
        """;

    private sealed record View(double Zoom, double X, double Y, NodeView[] Nodes);
    private sealed record NodeView(string Id, bool FullyInside);
    private static IEnumerable<string> Ancestors(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent) yield return directory.FullName;
    }

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }
}
