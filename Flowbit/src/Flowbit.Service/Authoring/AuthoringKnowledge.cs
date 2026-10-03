using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Shared.Authoring;

namespace Flowbit.Service.Authoring;

public interface IAuthoringKnowledge
{
    string ContractHash { get; }
    IReadOnlyDictionary<string, string> Resources { get; }
    string GetPromptContext(string? query = null);
    byte[] GetPackageZip();
}

public sealed record AuthoringPackageManifest(int FormatVersion, string SchemaHash, string ContractHash,
    SortedDictionary<string, string> Resources, AuthoringPackageCompatibility Compatibility);

public sealed record AuthoringPackageCompatibility(string ModelType, IReadOnlyList<string> NodeTypes);

/// <summary>Loads only the immutable package shipped with this application, never arbitrary request paths.</summary>
public sealed class AuthoringKnowledge : IAuthoringKnowledge
{
    private readonly byte[] zip;
    public string ContractHash { get; }
    public IReadOnlyDictionary<string, string> Resources { get; }

    public AuthoringKnowledge() : this(Path.Combine(AppContext.BaseDirectory, "authoring", "flowbit-authoring")) { }

    public AuthoringKnowledge(string packageDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        var manifest = JsonSerializer.Deserialize<AuthoringPackageManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")))
            ?? throw new InvalidOperationException("The Flowbit authoring package manifest is missing.");
        if (manifest.FormatVersion != 1 || manifest.Resources is null || manifest.SchemaHash != AuthoringPackageBuilder.Hash(WorkflowAuthoringJson.CreateSchema()))
            throw new InvalidOperationException("The Flowbit authoring package does not match the running workflow model. Rebuild the API.");
        var resources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, expectedHash) in manifest.Resources)
        {
            if (relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or "..") || Path.IsPathRooted(relative))
                throw new InvalidOperationException("Invalid path in the authoring package manifest.");
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid path in the authoring package manifest.");
            var content = File.ReadAllText(path);
            if (AuthoringPackageBuilder.Hash(content) != expectedHash) throw new InvalidOperationException($"Authoring resource '{relative}' failed its integrity check.");
            resources.Add(relative, content);
        }
        if (AuthoringPackageBuilder.ContractHash(manifest.Resources) != manifest.ContractHash)
            throw new InvalidOperationException("Authoring contract hash does not match its resources.");
        foreach (var required in CoreResources)
            if (!resources.ContainsKey(required)) throw new InvalidOperationException($"Authoring resource '{required}' is missing.");
        if (CoreResources.Sum(name => (long)resources[name].Length) > 1_000_000)
            throw new InvalidOperationException("The core authoring knowledge exceeds the 1,000,000-character package limit. Review the package before deployment.");
        if (manifest.Compatibility is null || manifest.Compatibility.ModelType != typeof(Flowbit.Shared.Models.WorkflowModel).FullName
            || manifest.Compatibility.NodeTypes is null || !manifest.Compatibility.NodeTypes.SequenceEqual(WorkflowAuthoringJson.NodeTypes))
            throw new InvalidOperationException("Authoring package compatibility does not match this Flowbit release.");
        if (AuthoringPackageBuilder.Hash(resources["references/workflow.schema.json"]) != manifest.SchemaHash)
            throw new InvalidOperationException("Packaged schema does not match the declared model contract.");
        ContractHash = manifest.ContractHash;
        // Recreate the same deterministic ZIP from verified bytes, excluding unlisted files.
        resources["manifest.json"] = File.ReadAllText(Path.Combine(directory, "manifest.json"));
        Resources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(resources);
        zip = AuthoringPackageBuilder.CreateZip(resources);
    }

    public byte[] GetPackageZip() => (byte[])zip.Clone();

    private static readonly string[] CoreResources =
    [
        "SKILL.md", "references/authoring-guide.md", "references/capabilities.json", "references/workflow.schema.json",
        "references/docs/node-reference.md", "references/docs/bpmn-support.md", "references/docs/developer-guide.md"
    ];

    public string GetPromptContext(string? query = null)
    {
        var result = new StringBuilder();
        // The API model cannot follow local file links. Always provide the complete authored behavior
        // contract, independent of the user's language; keyword matching must not hide a capability.
        foreach (var name in CoreResources)
            Append(name, Resources[name]);
        var terms = Regex.Matches(query ?? string.Empty, @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var remaining = 40000;
        foreach (var example in Resources.Where(item => item.Key.StartsWith("examples/", StringComparison.Ordinal) && item.Key.EndsWith(".json", StringComparison.Ordinal))
                     .Select(item => new { item.Key, item.Value, Score = terms.Count(term => item.Key.Contains(term, StringComparison.OrdinalIgnoreCase)) })
                     .OrderByDescending(item => item.Score).ThenBy(item => item.Key, StringComparer.Ordinal).Take(2))
        {
            if (example.Value.Length > remaining) continue;
            Append(example.Key, example.Value);
            remaining -= example.Value.Length;
        }
        return result.ToString();

        void Append(string name, string content) => result.Append("\n--- ").Append(name).Append(" ---\n").Append(content).Append('\n');
    }
}

/// <summary>Build-time export shared by tests and the small command-line exporter.</summary>
public static class AuthoringPackageBuilder
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static SortedDictionary<string, string> CreateResources(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        Add("SKILL.md", "authoring/flowbit-authoring/SKILL.md");
        Add("references/authoring-guide.md", "authoring/flowbit-authoring/references/authoring-guide.md");
        foreach (var directory in new[] { "docs", "examples" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".md" or ".json").Order(StringComparer.Ordinal))
        {
            var source = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (source.StartsWith("docs/refactoring/", StringComparison.Ordinal)) continue;
            Add(directory == "examples" ? source : "references/" + source, source);
        }
        Add("references/Flowbit/README.md", "Flowbit/README.md");
        result["references/workflow.schema.json"] = WorkflowAuthoringJson.CreateSchema();
        result["references/capabilities.json"] = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            nodeTypes = WorkflowAuthoringJson.NodeTypes,
            authoringReference = "docs/node-reference.md",
            examples = result.Keys.Where(key => key.StartsWith("examples/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal)).Select(key => "../" + key).ToArray(),
            capabilities = new[] { "all canonical node and sequence-flow properties", "typed variables and shared bindings", "roles, claims, assignment and inbox visibility", "NCalc and JavaScript", "REST and messages", "multi-instance and FlowInfo", "timers, jobs and conditional events", "business keys and idempotency", "attributes and external IDs" }
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        // Preserve the owning documents' relative structure. References to source-only code/screenshots
        // are rendered as labels, so this package never requires a repository checkout to read its guides.
        foreach (var path in result.Keys.Where(key => key.EndsWith(".md", StringComparison.Ordinal)).ToArray())
            result[path] = Regex.Replace(result[path], @"(!?\[[^\]\r\n]*\])\(([^)\r\n]+)\)", match =>
            {
                var target = match.Groups[2].Value;
                if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _)) return match.Value;
                var filePart = Uri.UnescapeDataString(target.Split('#')[0]);
                if (filePart.Length == 0) return match.Value;
                var resolved = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(path)!, filePart))).Replace('\\', '/');
                if (result.ContainsKey(resolved)) return match.Value;
                var sourceTarget = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(sources[path])!, filePart))).Replace('\\', '/');
                var packagedTarget = sources.FirstOrDefault(item => item.Value == sourceTarget).Key;
                if (packagedTarget is not null)
                {
                    var relative = Path.GetRelativePath(Path.Combine(root, Path.GetDirectoryName(path)!), Path.Combine(root, packagedTarget)).Replace('\\', '/');
                    var fragment = target.IndexOf('#');
                    return match.Groups[1].Value + "(" + relative + (fragment >= 0 ? target[fragment..] : "") + ")";
                }
                return match.Groups[1].Value.TrimStart('!').Trim('[', ']');
            });
        var hashes = new SortedDictionary<string, string>(result.ToDictionary(item => item.Key, item => Hash(item.Value)), StringComparer.Ordinal);
        var manifest = new AuthoringPackageManifest(1, Hash(result["references/workflow.schema.json"]), ContractHash(hashes), hashes,
            new(typeof(Flowbit.Shared.Models.WorkflowModel).FullName!, WorkflowAuthoringJson.NodeTypes));
        result["manifest.json"] = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        return result;

        void Add(string name, string source)
        {
            sources.Add(name, source.Replace('\\', '/'));
            result.Add(name, File.ReadAllText(Path.Combine(root, source)).Replace("\r\n", "\n").Replace('\r', '\n'));
        }
    }

    public static void Export(string repositoryRoot, string outputDirectory)
    {
        var resources = CreateResources(repositoryRoot);
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), "flowbit-authoring");
        var sourceDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "authoring", "flowbit-authoring"));
        if (directory.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)
            || sourceDirectory.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The generated package cannot replace its source folder.");
        if (Directory.Exists(directory))
        {
            if (!File.Exists(Path.Combine(directory, "manifest.json")) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException("The package output must be empty or a previous generated package.");
            // Only the verified exact generated package directory is owned by the exporter.
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid authoring export path.");
                if (!resources.ContainsKey(Path.GetRelativePath(directory, path).Replace('\\', '/'))) File.Delete(path);
            }
        }
        foreach (var (relative, content) in resources)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8);
        }
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, "flowbit-authoring.zip"), CreateZip(resources));
    }

    public static byte[] CreateZip(IEnumerable<KeyValuePair<string, string>> resources)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (var (name, content) in resources.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry("flowbit-authoring/" + name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var writer = new StreamWriter(entry.Open(), Utf8);
            writer.Write(content);
        }
        return stream.ToArray();
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(value)));
    public static string ContractHash(IEnumerable<KeyValuePair<string, string>> resources) => Hash(string.Join("\n", resources.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + ":" + item.Value)));
}
