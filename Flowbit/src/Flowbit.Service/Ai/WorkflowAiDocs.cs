using System.Text;
using Flowbit.Service.Authoring;

namespace Flowbit.Service.Ai;

/// <summary>
/// The documentation the authoring agent reads through its file tools: the doc set Flowbit AI Lab v3 used (six guides plus the
/// canonical examples and their READMEs), taken from the verified authoring package under the lab's paths. Nothing else in the
/// package, such as the API guide or the skill's own instructions, is visible.
/// </summary>
public sealed class WorkflowAiDocs
{
    public static readonly IReadOnlyList<string> Guides =
        ["bpmn-support.md", "deployment.md", "developer-guide.md", "getting-started.md", "index.md", "node-reference.md"];

    public WorkflowAiDocs(IAuthoringKnowledge knowledge)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var guide in Guides)
            files[guide] = knowledge.Resources.TryGetValue("references/docs/" + guide, out var text)
                ? text : throw new InvalidOperationException($"The authoring package has no references/docs/{guide} for the AI assistant.");
        foreach (var (path, text) in knowledge.Resources.Where(item => item.Key.StartsWith("examples/", StringComparison.Ordinal)))
            files[path] = text;
        if (!files.Keys.Any(path => path.EndsWith(".json", StringComparison.Ordinal)))
            throw new InvalidOperationException("The authoring package has no example workflows for the AI assistant.");
        Files = files;
        // One "path sha256" line per file in ordinal path order, so any added, removed or changed file changes it.
        Fingerprint = AuthoringPackageBuilder.Hash(string.Concat(files.Select(item => item.Key + " " + AuthoringPackageBuilder.Hash(item.Value) + "\n")));
    }

    public IReadOnlyDictionary<string, string> Files { get; }
    public string Fingerprint { get; }
}
