using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>Loads and round-trips the canonical examples through real editor controls.</summary>
[Collection(ExampleAcceptanceCollection.Name)]
public sealed class ExampleWorkflowEditorTests(ExampleAcceptanceFixture fixture)
{
    public static TheoryData<string, int, int> ExamplesAndViewports
    {
        get
        {
            var cases = new TheoryData<string, int, int>();
            foreach (var path in new[]
            {
                "durable-jobs/02-retries-and-incident-recovery.json",
                "durable-jobs/03-error-boundary-precedence.json",
                "messages/03-message-catch-with-timeout.json",
                "gateways/07-nested-scope-cancellation.json",
                "gateways/08-terminate-live-siblings.json",
                "multi-instance/07-role-management-active-and-pending.json",
            })
            {
                cases.Add(path, 1440, 900);
                cases.Add(path, 1024, 768);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ExamplesAndViewports))]
    public async Task CanonicalExampleLoadsInspectsSavesAndReloads(string relativePath, int width, int height)
    {
        var stack = fixture.Stack;
        var name = $"example-editor-{Path.GetFileNameWithoutExtension(relativePath)}-{width}x{height}";
        await using var scenario = await stack.CreateScenario(name, width, height, blockNativeFilePicker: true);
        scenario.ExpectDialogStartingWith(IdentityScreen.FallbackAlertPrefix);
        await scenario.RunAsync("canonical-editor-roundtrip", async () =>
        {
            var source = Path.Combine(stack.RepositoryRoot, "examples", relativePath);
            using var authored = JsonDocument.Parse(await File.ReadAllTextAsync(source, ScenarioCancellation.Token));
            await scenario.OpenEditorAsync(stack.EditorBaseAddress);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(page, source);
            await EditorInteractions.PinInspectorOpenAsync(page);
            await page.Locator("#fitViewBtn").ClickAsync();

            var nodes = authored.RootElement.GetProperty("flowNodes").EnumerateArray().ToArray();
            foreach (var node in nodes)
                await Assertions.Expect(page.Locator($"#nodes .node[data-id='{node.GetProperty("id").GetInt32()}']"))
                    .ToBeAttachedAsync();

            // Each distinct task/event/gateway kind gets a real canvas click and inspector check.
            foreach (var node in nodes.GroupBy(node => node.GetProperty("type").GetString()).Select(group => group.First()))
            {
                var id = node.GetProperty("id").GetInt32();
                await EditorInteractions.SelectNodeAsync(page, id);
                await Assertions.Expect(page.Locator("#inspector")).ToContainTextAsync($"Node #{id}");
            }
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "diagram-and-inspector.png"), FullPage = true,
            });

            using var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(page, scenario, scenario.ArtifactDirectory);
            Assert.Equal(authored.RootElement.GetProperty("id").GetString(), saved.RootElement.GetProperty("id").GetString());
            Assert.Equal(NodeContracts(authored.RootElement), NodeContracts(saved.RootElement));
            Assert.Equal(FlowContracts(authored.RootElement), FlowContracts(saved.RootElement));
            Assert.True(JsonNode.DeepEquals(SemanticConfiguration(authored.RootElement), SemanticConfiguration(saved.RootElement)),
                "Saving the example changed its execution configuration.");

            await page.ReloadAsync();
            await page.WaitForSelectorAsync("#svg");
            await EditorInteractions.LoadSavedFileAsync(page, scenario.LastSavedFile!);
            await page.Locator("#fitViewBtn").ClickAsync();
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(nodes.Length);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "reloaded-diagram.png"), FullPage = true,
            });
        });
    }

    private static string[] NodeContracts(JsonElement model) => model.GetProperty("flowNodes").EnumerateArray()
        .Select(node => $"{node.GetProperty("id")}:{node.GetProperty("type")}:{node.GetProperty("name")}")
        .Order(StringComparer.Ordinal).ToArray();

    private static string[] FlowContracts(JsonElement model) => model.GetProperty("sequenceFlows").EnumerateArray()
        .Select(flow => $"{flow.GetProperty("id")}:{flow.GetProperty("sourceRef")}:{flow.GetProperty("targetRef")}")
        .Order(StringComparer.Ordinal).ToArray();

    private static JsonNode SemanticConfiguration(JsonElement json)
    {
        var model = json.Deserialize<WorkflowModel>()!;
        WorkflowModelMigrator.Normalize(model);
        var result = JsonSerializer.SerializeToNode(model)!.AsObject();
        // The editor may grow lanes or position attached boundaries when rendering.
        // Compare all execution settings, including policies, mappings and role sources.
        result.Remove("lanes");
        foreach (var node in result["flowNodes"]!.AsArray())
        {
            node!.AsObject().Remove("x");
            node.AsObject().Remove("y");
        }
        return result;
    }
}
