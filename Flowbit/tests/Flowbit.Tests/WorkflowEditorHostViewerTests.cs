using System.Text.Json;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowEditorHostViewerTests
{
    [Fact]
    public async Task ViewerMountsReadOnlyForwardsSelectionAndStopsAfterDisposal()
    {
        using var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("""
            const calls = [];
            let mounted = null;
            const document = { body: {}, createElement: () => ({ remove() {} }), head: { append: element => element.onload() } };
            const MutationObserver = class { observe() {} disconnect() {} };
            const TextDecoder = class { decode(value) { return value; } };
            const window = { FlowbitEditor: { mount: (root, options) => {
              mounted = options;
              return {
                load(definition, overlay) { calls.push(['load', definition.id, overlay.nodes.length]); },
                setOverlay(overlay) { calls.push(['setOverlay', overlay.nodes.length]); },
                selectNode(nodeId) { calls.push(['selectNode', nodeId]); },
                dispose() { calls.push(['dispose']); }
              };
            } } };
            """);
        engine.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-editor-host.js"))
            .Replace("export async function create", "async function create", StringComparison.Ordinal));
        var result = await engine.EvaluateAsync("""
            (async () => {
              const forwarded = [];
              const dotnet = { invokeMethodAsync: (method, nodeId) => { forwarded.push([method, nodeId]); return Promise.resolve(); } };
              const host = await createViewer({ isConnected: true }, dotnet,
                { template: 'template', stylesheet: 'css', script: 'script' }, 'Instance diagram');
              await host.load({ arrayBuffer: () => Promise.resolve('{"id":"wf"}') }, { nodes: [{ id: 3 }] });
              host.setOverlay({ nodes: [] });
              host.selectNode(null);
              mounted.onSelectionChange(3);
              host.dispose();
              mounted.onSelectionChange(4);
              host.setOverlay({ nodes: [] });
              host.selectNode(5);
              await host.load({ arrayBuffer: () => Promise.resolve('{"id":"late"}') }, { nodes: [] });
              host.dispose();
              return JSON.stringify({ mode: mounted.mode, label: mounted.label, calls, forwarded });
            })()
            """);

        using var json = JsonDocument.Parse(result.AsString());
        var root = json.RootElement;
        Assert.Equal("viewer", root.GetProperty("mode").GetString());
        Assert.Equal("Instance diagram", root.GetProperty("label").GetString());
        Assert.Equal(
            """[["load","wf",1],["setOverlay",0],["selectNode",null],["dispose"]]""",
            root.GetProperty("calls").GetRawText());
        Assert.Equal("""[["DiagramSelectionChanged",3]]""", root.GetProperty("forwarded").GetRawText());
    }
}
