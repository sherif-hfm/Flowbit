using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAuthoringPackageTests
{
    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "authoring", "flowbit-authoring", "SKILL.md"))
                    && File.Exists(Path.Combine(directory.FullName, "Flowbit", "src", "Flowbit.Shared", "Flowbit.Shared.csproj"))) return directory.FullName;
            throw new InvalidOperationException("The authoring source package was not found.");
        }
    }

    [Theory]
    [MemberData(nameof(ExampleWorkflowData.All), MemberType = typeof(ExampleWorkflowData))]
    public void CanonicalExamples_PassStrictContractAndRuntimeValidation(string relativePath)
    {
        var model = WorkflowAuthoringJson.Parse(ExampleWorkflowData.Read(relativePath));
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions());
        validator.ValidateAuthored(model);
        WorkflowModelMigrator.Normalize(model);
        validator.ValidateNormalized(model);
    }

    [Fact]
    public void Schema_CoversTheEntireSerializedModelAndCurrentNodeCatalog()
    {
        using var document = JsonDocument.Parse(WorkflowAuthoringJson.CreateSchema());
        var definitions = document.RootElement.GetProperty("$defs");
        var pending = new Queue<Type>();
        var seen = new HashSet<Type>();
        pending.Enqueue(typeof(WorkflowModel));
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            var declared = definitions.GetProperty(type.Name).GetProperty("properties");
            var expected = type.GetProperties().Where(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null &&
                property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always &&
                !property.Name.StartsWith("Legacy", StringComparison.Ordinal) &&
                !(type == typeof(MessageCatchModel) && property.Name == nameof(MessageCatchModel.IdempotencyVariable))).ToArray();
            Assert.Equal(expected.Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name).Order(),
                declared.EnumerateObject().Select(property => property.Name).Order());
            foreach (var property in expected)
            {
                var nested = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (nested.IsGenericType && nested.GetGenericTypeDefinition() == typeof(List<>)) nested = nested.GetGenericArguments()[0];
                if (nested.IsClass && nested != typeof(string)) pending.Enqueue(nested);
            }
        }
        var nodes = definitions.GetProperty(nameof(FlowNodeModel)).GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(21, nodes.Length);
        Assert.Equal(WorkflowAuthoringJson.NodeTypes, nodes);
        Assert.All(nodes, type => Assert.True(BpmnFlowNodeTypes.IsSupported(type!)));
        Assert.False(definitions.GetProperty(nameof(MessageCatchModel)).GetProperty("properties").TryGetProperty("idempotencyVariable", out _));
        var allExampleTypes = ExampleWorkflowData.RelativePaths.SelectMany(path => WorkflowAuthoringJson.Parse(ExampleWorkflowData.Read(path)).FlowNodes)
            .Select(node => node.Type).Distinct().Order().ToArray();
        Assert.Equal(WorkflowAuthoringJson.NodeTypes, allExampleTypes);
    }

    [Theory]
    [InlineData("{\"id\":\"a\",\"id\":\"b\",\"name\":\"Draft\",\"flowNodes\":[],\"sequenceFlows\":[]}")]
    [InlineData("{\"id\":\"a\",\"name\":\"Draft\",\"flowNodes\":null,\"sequenceFlows\":[]}")]
    [InlineData("{\"id\":\"a\",\"name\":\"Draft\",\"flowNodes\":[],\"sequenceFlows\":[],\"steps\":[]}")]
    [InlineData("{\"id\":\"a\",\"name\":\"Draft\",\"flowNodes\":[{\"id\":1,\"name\":\"a\",\"type\":\"madeUp\"}],\"sequenceFlows\":[]}")]
    [InlineData("{\"id\":\"a\",\"name\":\"Draft\",\"flowNodes\":[{\"id\":1,\"name\":\"a\",\"type\":\"userTask\",\"multiInstance\":{\"mode\":\"paralell\"}}],\"sequenceFlows\":[]}")]
    [InlineData("{\"id\":\"a\",\"name\":\"Draft\",\"flowNodes\":[{\"id\":1,\"name\":\"a\",\"type\":\"messageStartEvent\",\"message\":{\"idempotencyVariable\":\"key\"}}],\"sequenceFlows\":[]}")]
    public void StrictParser_RejectsMalformedAndLegacyAuthoring(string json) => Assert.Throws<JsonException>(() => WorkflowAuthoringJson.Parse(json));

    [Fact]
    public async Task Parser_AllowsBusinessJsonAndIsSafeForConcurrentRequests()
    {
        const string json = """
            {"id":"draft","name":"Draft","flowNodes":[],"sequenceFlows":[],"variables":[
              {"id":1,"name":"payload","dataType":"json","defaultValue":{"customer":{"anything":true},"items":[null,3]}}
            ]}
            """;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            var model = WorkflowAuthoringJson.Parse(json);
            Assert.True(model.Variables[0].DefaultValue!.Value.GetProperty("customer").GetProperty("anything").GetBoolean());
            Assert.NotEmpty(WorkflowAuthoringJson.CreateSchema());
        })));
    }

    [Fact]
    public void Export_IsDeterministicSelfContainedAndIdenticalToDownload()
    {
        using var output = new TemporaryDirectory();
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        var original = File.ReadAllBytes(System.IO.Path.Combine(output.Path, "flowbit-authoring.zip"));
        var package = System.IO.Path.Combine(output.Path, "flowbit-authoring");
        File.WriteAllText(System.IO.Path.Combine(package, "stale-reference.md"), "Old release");
        var staleFolder = System.IO.Path.Combine(package, "references", "docs", "plans");
        Directory.CreateDirectory(staleFolder);
        File.WriteAllText(System.IO.Path.Combine(staleFolder, "old-plan.md"), "Old release");
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        Assert.False(File.Exists(System.IO.Path.Combine(package, "stale-reference.md")));
        Assert.False(Directory.Exists(staleFolder));
        Assert.Equal(original, File.ReadAllBytes(System.IO.Path.Combine(output.Path, "flowbit-authoring.zip")));
        var loaded = new AuthoringKnowledge(package);
        Assert.Equal(original, loaded.GetPackageZip());
        Assert.Equal(ExampleWorkflowData.RelativePaths.Order(StringComparer.Ordinal),
            loaded.Resources.Keys.Where(path => path.StartsWith("examples/", StringComparison.Ordinal)
                && path.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        using var archive = new ZipArchive(new MemoryStream(original));
        Assert.Equal(loaded.Resources.Keys.Select(key => "flowbit-authoring/" + key).Order(), archive.Entries.Select(entry => entry.FullName).Order());
        foreach (var entry in archive.Entries)
        {
            Assert.Equal(1980, entry.LastWriteTime.Year);
            Assert.Equal(loaded.Resources[entry.FullName["flowbit-authoring/".Length..]], new StreamReader(entry.Open()).ReadToEnd());
        }
        foreach (var (name, text) in loaded.Resources.Where(item => item.Key.EndsWith(".md", StringComparison.Ordinal)))
        foreach (Match link in Regex.Matches(text, @"!?\[[^\]\r\n]*\]\(([^)\r\n]+)\)"))
        {
            var target = link.Groups[1].Value;
            if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _)) continue;
            var filePart = Uri.UnescapeDataString(target.Split('#')[0]);
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(package, System.IO.Path.GetDirectoryName(name)!, filePart));
            Assert.StartsWith(package + System.IO.Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path), $"Broken package link in {name}: {target}");
        }
        Assert.Contains("(../../examples/README.md)", loaded.Resources["references/docs/node-reference.md"]);
        Assert.Contains("(../references/docs/getting-started.md)", loaded.Resources["examples/README.md"]);
        // Planning records describe decisions, superseded engines and unbuilt enhancements; links to them become labels
        // (checked by the loop above).
        Assert.DoesNotContain(loaded.Resources.Keys, key => key.StartsWith("references/docs/plans/", StringComparison.Ordinal));
        Assert.DoesNotContain(loaded.Resources.Keys, key => key.StartsWith("references/docs/enhancements/", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthoringGuide_StatesTheEditorShapeSizes()
    {
        // The editor does not lay out an imported diagram, so agents place every shape from these sizes.
        var editor = EditorSource.Read();
        int Constant(string name)
        {
            var match = Regex.Match(editor, $@"\b{name} = (\d+)");
            Assert.True(match.Success, $"{name} was not found in flowbit-editor.js.");
            return int.Parse(match.Groups[1].Value);
        }
        var guide = new AuthoringKnowledge().Resources["references/authoring-guide.md"];
        Assert.Contains($"are {Constant("NODE_W")} x {Constant("NODE_H")}.", guide);
        Assert.Contains($"are {Constant("EVENT_D")} x {Constant("EVENT_D")} and gateways {Constant("GATEWAY_D")} x {Constant("GATEWAY_D")};", guide);
        Assert.Contains($"minimum of {Constant("MIN_LANE_W")} x {Constant("MIN_LANE_H")};", guide);
    }

    [Fact]
    public void Package_RejectsTamperingAndIncompatibleSchema()
    {
        using var output = new TemporaryDirectory();
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        var package = System.IO.Path.Combine(output.Path, "flowbit-authoring");
        File.AppendAllText(System.IO.Path.Combine(package, "SKILL.md"), "tampered");
        Assert.Throws<InvalidOperationException>(() => new AuthoringKnowledge(package));
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        var manifestPath = System.IO.Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["SchemaHash"] = "different-model";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => new AuthoringKnowledge(package));
    }

    [Fact]
    public void PackagedResources_AreCopiedToReferencingApplicationOutput()
    {
        var package = new AuthoringKnowledge();
        Assert.NotEmpty(package.ContractHash);
        Assert.NotEmpty(package.GetPackageZip());
    }

    [Fact]
    public void ExportedPackage_GivesTheAgentEveryNodeTypesReferenceAndTheExamples()
    {
        using var output = new TemporaryDirectory();
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        var docs = new Flowbit.Service.Ai.WorkflowAiDocs(new AuthoringKnowledge(System.IO.Path.Combine(output.Path, "flowbit-authoring")));
        foreach (var node in WorkflowAuthoringJson.NodeTypes) Assert.Contains("### " + node + "\n", docs.Files["node-reference.md"]);
        Assert.Contains("examples/basics/02-two-step-approval.json", docs.Files.Keys);
        Assert.Contains("developer-guide.md", docs.Files.Keys);
    }

    [Fact]
    public void NewDiagramLayout_KeepsLoopsInTheirColumnStacksParallelBranchesAndKeepsLanesCompact()
    {
        // Values worked out by hand in Flowbit AI Lab v3 from the compact layout rules. Columns: a task's centre is 290 px from a
        // neighbouring task's, 227 px from an event's and 233 px from a gateway's; rows share a centre line 99 px below the lane top.

        // The rework flow 3 -> 2 goes back to an earlier node, so task 2 keeps column 1; boundary event 5 sits at its host, task 3.
        var loop = LaidOut("examples/basics/02-two-step-approval.json");
        Assert.Equal(new[] { (1, 123, 97), (2, 287, 85), (3, 577, 85), (4, 867, 97), (5, 577, 85) }, Positions(loop));
        Assert.Empty(loop.Lanes);

        // The three parallel reviews share one column and stack in rows; the lane is only as tall as those three rows.
        var parallel = LaidOut("examples/gateways/02-parallel-fork-and-join.json");
        Assert.Equal(new[] { (1, 123, 97), (2, 297, 91), (3, 473, 85), (4, 473, 265), (5, 473, 383), (6, 763, 91), (7, 939, 85), (8, 1229, 97) },
            Positions(parallel));
        Assert.Equal(new[] { (1, 40, 30, 1336, 466) }, parallel.Lanes.Select(lane => (lane.Id, lane.X, lane.Y, lane.W, lane.H)));

        // Two lanes of the same width, 30 px apart; the specialist's task keeps its place in the flow in the second lane.
        var lanes = LaidOut("examples/user-tasks/02-claim-inheritance.json");
        Assert.Equal(new[] { (1, 123, 97), (2, 287, 85), (3, 577, 85), (4, 867, 345), (5, 1157, 85), (6, 1447, 97) }, Positions(lanes));
        Assert.Equal(new[] { (1, 40, 30, 1554, 230), (2, 40, 290, 1554, 168) }, lanes.Lanes.Select(lane => (lane.Id, lane.X, lane.Y, lane.W, lane.H)));

        // An original without flow nodes, such as the editor's empty new document, is a new diagram too.
        var empty = WorkflowAuthoringJson.Parse(ExampleWorkflowData.Read("examples/basics/02-two-step-approval.json"));
        WorkflowAuthoringLayout.Apply(empty, new WorkflowModel { Id = "new", Name = "New workflow" });
        Assert.Equal(Positions(loop), Positions(empty));
    }

    [Fact]
    public void NewDiagramLayout_PutsShapesInsideCompactLanesWithoutOverlapsAndChangesOnlyLayoutFields()
    {
        foreach (var path in ExampleWorkflowData.RelativePaths)
        {
            var authored = WorkflowAuthoringJson.Parse(ExampleWorkflowData.Read(path));
            // The problems seen in model-placed diagrams, provoked: every node at 0,0 and every lane 10 px square.
            var model = WorkflowAuthoringJson.Parse(ExampleWorkflowData.Read(path));
            foreach (var node in model.FlowNodes) (node.X, node.Y) = (0, 0);
            foreach (var lane in model.Lanes) (lane.X, lane.Y, lane.W, lane.H) = (0, 0, 10, 10);
            WorkflowAuthoringLayout.Apply(model);
            var arranged = JsonSerializer.Serialize(model);
            WorkflowAuthoringLayout.Apply(model);
            Assert.Equal(arranged, JsonSerializer.Serialize(model));
            Assert.Equal(WithoutLayout(authored), WithoutLayout(model));

            var shapes = model.FlowNodes.Where(node => !BpmnFlowNodeTypes.IsBoundary(node.Type)).ToDictionary(node => node.Id);
            // Each shape, caption included, lies inside its own lane.
            foreach (var node in shapes.Values.Where(node => node.LaneId is not null))
            {
                var visual = Visual(node);
                var lane = model.Lanes.Single(lane => lane.Id == node.LaneId);
                Assert.True(visual.L >= lane.X && visual.T >= lane.Y && visual.R <= lane.X + lane.W && visual.B <= lane.Y + lane.H,
                    $"{path}: node {node.Id} {visual} is not inside lane {lane.Id}");
            }
            // No shape or caption overlaps another node's shape or caption.
            var list = shapes.Values.ToArray();
            for (var i = 0; i < list.Length; i++)
                for (var j = i + 1; j < list.Length; j++)
                    Assert.True(Gap(Visual(list[i]), Visual(list[j])) > 0, $"{path}: nodes {list[i].Id} and {list[j].Id} overlap");
            // Linked shapes leave at least 100 px for the arrow.
            foreach (var flow in model.SequenceFlows)
                if (flow.SourceRef != flow.TargetRef && shapes.TryGetValue(flow.SourceRef, out var from) && shapes.TryGetValue(flow.TargetRef, out var to))
                    Assert.True(Gap(Box(from), Box(to)) >= 100, $"{path}: flow {flow.Id} links shapes only {Gap(Box(from), Box(to))} px apart");
            // Lanes share one width and are stacked in their declared order, 30 px apart.
            Assert.Single(model.Lanes.Select(lane => (lane.X, lane.W)).Distinct().DefaultIfEmpty());
            for (var i = 1; i < model.Lanes.Count; i++) Assert.Equal(model.Lanes[i - 1].Y + model.Lanes[i - 1].H + 30, model.Lanes[i].Y);
            // Boundary events sit at their host, in its lane.
            foreach (var boundary in model.FlowNodes.Where(node => BpmnFlowNodeTypes.IsBoundary(node.Type)))
            {
                var host = shapes[boundary.AttachedToRef!.Value];
                Assert.Equal((host.X, host.Y, host.LaneId), (boundary.X, boundary.Y, boundary.LaneId));
            }
        }
    }

    [Fact]
    public void NewDiagramLayout_PlacesNodesWithoutALaneInABandBelowTheLanes()
    {
        var model = new WorkflowModel
        {
            Lanes = [new() { Id = 1, Name = "Operations" }, new() { Id = 2, Name = "Other" }],
            FlowNodes =
            [
                Node(1, "startEvent", 1), Node(2, "serviceTask", 1), Node(3, "userTask", 1), Node(4, "endEvent", 1),
                new() { Id = 5, Name = "Failure", Type = "errorBoundaryEvent", AttachedToRef = 2 },
                Node(6, "errorEndEvent", 1), Node(7, "startEvent", 2), Node(8, "task", null)
            ],
            SequenceFlows = [Flow(1, 1, 2), Flow(2, 2, 3), Flow(3, 3, 2), Flow(4, 3, 4), Flow(5, 5, 6)]
        };
        WorkflowAuthoringLayout.Apply(model);
        Assert.True(model.FlowNodes.Single(node => node.Id == 4).X > model.FlowNodes.Single(node => node.Id == 3).X);
        Assert.True(model.Lanes[1].Y >= model.Lanes[0].Y + model.Lanes[0].H);
        Assert.True(model.FlowNodes.Single(node => node.Id == 8).Y > model.Lanes[1].Y + model.Lanes[1].H);
        var boundary = model.FlowNodes.Single(node => node.Id == 5);
        var host = model.FlowNodes.Single(node => node.Id == 2);
        Assert.Equal((host.LaneId, host.X, host.Y), (boundary.LaneId, boundary.X, boundary.Y));
    }

    private static WorkflowModel LaidOut(string example)
    {
        var model = WorkflowAuthoringJson.Parse(ExampleWorkflowData.Read(example));
        WorkflowAuthoringLayout.Apply(model);
        return model;
    }

    private static (int Id, int X, int Y)[] Positions(WorkflowModel model) => model.FlowNodes.Select(node => (node.Id, node.X, node.Y)).ToArray();

    private static string WithoutLayout(WorkflowModel model)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(model))!;
        foreach (var node in root["flowNodes"]!.AsArray().Select(node => node!.AsObject()))
        {
            node.Remove("x"); node.Remove("y");
            if (BpmnFlowNodeTypes.IsBoundary((string)node["type"]!)) node.Remove("laneId");
        }
        foreach (var lane in root["lanes"]!.AsArray().Select(lane => lane!.AsObject())) { lane.Remove("x"); lane.Remove("y"); lane.Remove("w"); lane.Remove("h"); }
        return root.ToJsonString();
    }

    // Shape sizes in the editor (flowbit-editor.js); x/y is the top-left corner.
    private static (int L, int T, int R, int B) Box(FlowNodeModel node)
    {
        var (w, h) = node.Type.EndsWith("Gateway", StringComparison.Ordinal) ? (76, 76) : node.Type.EndsWith("Event", StringComparison.Ordinal) ? (64, 64) : (190, 88);
        return (node.X, node.Y, node.X + w, node.Y + h);
    }

    // The shape plus the caption the editor writes under events and gateways (edgeNodeBounds in flowbit-editor.js).
    private static (int L, int T, int R, int B) Visual(FlowNodeModel node)
    {
        var box = Box(node);
        if (!node.Type.EndsWith("Gateway", StringComparison.Ordinal) && !node.Type.EndsWith("Event", StringComparison.Ordinal)) return box;
        var middle = (box.L + box.R) / 2;
        return (middle - 75, box.T, middle + 75, box.B + (node.Type == "startEvent" ? 74 : 62));
    }

    // Positive when the two boxes are apart: the larger of the horizontal and vertical gaps.
    private static int Gap((int L, int T, int R, int B) a, (int L, int T, int R, int B) b) =>
        Math.Max(Math.Max(b.L - a.R, a.L - b.R), Math.Max(b.T - a.B, a.T - b.B));

    [Fact]
    public void Editing_PreservesOriginalPositionsAndPlacesAdditionsOutsideOccupiedSpace()
    {
        var original = new WorkflowModel
        {
            Lanes = [new() { Id = 1, Name = "Existing", X = 20, Y = 20, W = 600, H = 260 }],
            FlowNodes = [new() { Id = 1, Name = "Task", Type = "userTask", LaneId = 1, X = 120, Y = 100 }]
        };
        var edited = JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(original))!;
        edited.FlowNodes[0].X = 0;
        edited.FlowNodes[0].Y = 0;
        edited.FlowNodes.AddRange([Node(2, "task", 1), Node(3, "task", 1)]);
        WorkflowAuthoringLayout.Apply(edited, original);
        Assert.Equal((120, 100), (edited.FlowNodes[0].X, edited.FlowNodes[0].Y));
        Assert.Equal(260, edited.Lanes[0].H);
        Assert.All(edited.FlowNodes.Skip(1), node => Assert.True(node.X >= 420));
        Assert.True(Math.Abs(edited.FlowNodes[1].X - edited.FlowNodes[2].X) >= 250);
    }

    private static FlowNodeModel Node(int id, string type, int? lane) => new() { Id = id, Name = type, Type = type, LaneId = lane };
    private static SequenceFlowModel Flow(int id, int source, int target) => new() { Id = id, SourceRef = source, TargetRef = target };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flowbit-authoring-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
