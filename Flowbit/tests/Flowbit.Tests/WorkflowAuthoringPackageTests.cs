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
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        Assert.False(File.Exists(System.IO.Path.Combine(package, "stale-reference.md")));
        Assert.Equal(original, File.ReadAllBytes(System.IO.Path.Combine(output.Path, "flowbit-authoring.zip")));
        var loaded = new AuthoringKnowledge(package);
        Assert.Equal(original, loaded.GetPackageZip());
        Assert.Equal(42, loaded.Resources.Count(item => item.Key.StartsWith("examples/", StringComparison.Ordinal) && item.Key.EndsWith(".json", StringComparison.Ordinal)));
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
    public void ArabicRequest_RetainsAllNodeBehaviorAndExampleContext()
    {
        using var output = new TemporaryDirectory();
        AuthoringPackageBuilder.Export(RepositoryRoot, output.Path);
        var package = new AuthoringKnowledge(System.IO.Path.Combine(output.Path, "flowbit-authoring"));
        var context = package.GetPromptContext("إنشاء سير عمل للمشتريات مع مراجعة وشرط انتظار");
        foreach (var node in WorkflowAuthoringJson.NodeTypes) Assert.Contains("### " + node + "\n", context);
        Assert.Contains("--- examples/", context);
        Assert.Contains("--- references/docs/developer-guide.md ---", context);
    }

    [Fact]
    public void Layout_HandlesLoopsDisconnectedStartsAndAttachedBoundariesDeterministically()
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
        var arranged = JsonSerializer.Serialize(model);
        WorkflowAuthoringLayout.Apply(model);
        Assert.Equal(arranged, JsonSerializer.Serialize(model));
        Assert.True(model.FlowNodes.Single(node => node.Id == 4).X > model.FlowNodes.Single(node => node.Id == 3).X);
        var nodes = model.FlowNodes.Where(node => !BpmnFlowNodeTypes.IsBoundary(node.Type)).ToArray();
        foreach (var node in nodes)
        foreach (var other in nodes.Where(other => other.Id != node.Id))
            Assert.True(Math.Abs(node.X - other.X) >= 250 || Math.Abs(node.Y - other.Y) >= 150);
        foreach (var lane in model.Lanes)
        foreach (var node in nodes.Where(node => node.LaneId == lane.Id))
        {
            Assert.InRange(node.X, lane.X, lane.X + lane.W - 190);
            Assert.InRange(node.Y, lane.Y, lane.Y + lane.H - 88);
        }
        Assert.True(model.Lanes[1].Y >= model.Lanes[0].Y + model.Lanes[0].H);
        var boundary = model.FlowNodes.Single(node => node.Id == 5);
        var host = model.FlowNodes.Single(node => node.Id == 2);
        Assert.Equal((host.LaneId, host.X, host.Y), (boundary.LaneId, boundary.X, boundary.Y));
    }

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
