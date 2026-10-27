using System.Text.Json;
using Flowbit.Service.Ai;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The agent's six file tools, ported with Flowbit AI Lab v3's FileToolsTests; files live in memory.</summary>
public sealed class WorkflowAiWorkspaceTests
{
    private static WorkflowAiWorkspace Workspace(List<WorkflowAiToolActivity>? activity = null) => new(
        new WorkflowAiDocs(ResourceKnowledge.Small), null, null,
        new WorkflowAiWorkflowCheck(AgentHarness.Validator(), new WorkflowAiRedaction(), AgentHarness.Key, null), activity is null ? null : activity.Add);

    [Fact]
    public void TheDocsViewIsTheLabsDocSetFromTheRealPackageUnderTheLabsPaths()
    {
        var docs = new WorkflowAiDocs(new ResourceKnowledge(AuthoringRepository.Package));
        Assert.Equal(64, docs.Files.Count);
        Assert.Equal(WorkflowAiDocs.Guides, docs.Files.Keys.Where(path => !path.Contains('/')));
        Assert.Equal(49, docs.Files.Keys.Count(path => path.StartsWith("examples/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)));
        Assert.Contains("examples/gateways/02-parallel-fork-and-join.json", docs.Files.Keys);
        Assert.DoesNotContain(docs.Files.Keys, path => path.Contains("api-guide") || path.Contains("SKILL") || path.Contains("authoring-guide") || path.Contains("plans/"));
        Assert.Equal(64, docs.Fingerprint.Length);
        Assert.Equal(docs.Fingerprint, new WorkflowAiDocs(new ResourceKnowledge(AuthoringRepository.Package)).Fingerprint);
    }

    [Fact]
    public void TheFullListingShowsDocsThenSavedFilesAndSavedFilesCanBeRead()
    {
        var workspace = Workspace();
        var listing = workspace.GetFilesList();
        Assert.Equal(WorkflowAiDocs.Guides.Concat(["examples/README.md", "examples/basics/02-two-step-approval.json"]).Order(StringComparer.Ordinal),
            listing.Files.Select(file => file.Path));
        Assert.Equal(new WorkflowAiFileEntry("index.md", 21, 2), listing.Files.Single(file => file.Path == "index.md"));

        Assert.True(workspace.SaveAppendFile("workflow.json", "{\n  \"id\": \"x\"\n}\n").Success);
        Assert.True(workspace.SaveAppendFile("output/open-questions.md", "# Questions\n").Success);
        Assert.Equal(["output/open-questions.md", "output/workflow.json"], workspace.GetFilesList().Files.Skip(8).Select(file => file.Path));
        Assert.Equal(["output/open-questions.md", "output/workflow.json"], workspace.GetFilesList("output/").Files.Select(file => file.Path));
        Assert.Equal(["examples/basics/02-two-step-approval.json"], workspace.GetFilesList("examples/basics").Files.Select(file => file.Path));
        Assert.Equal("{\n  \"id\": \"x\"\n}\n", workspace.GetFile("output/workflow.json").Content);
        Assert.False(workspace.GetFilesList("examples/missing").Success);
    }

    [Fact]
    public void ReadsWholeFilesAndLineRangesExactlyAndRepeatsGetANotice()
    {
        var workspace = Workspace();
        var whole = workspace.GetFile("index.md");
        Assert.Equal((true, "# index.md\nLine two.\n", 2, 1, 2, false), (whole.Success, whole.Content, whole.TotalLines, whole.StartLine, whole.EndLine, whole.Truncated));
        var second = workspace.GetFile("index.md", 2, 1);
        Assert.Equal(("Line two.\n", (int?)null), (second.Content, second.NextStartLine));

        var repeated = workspace.GetFile("index.md");
        Assert.Null(repeated.Content);
        Assert.Equal("This exact content was already returned earlier in this conversation; use that earlier result instead of reading it again.", repeated.Notice);

        // A changed saved file is always returned fresh.
        workspace.SaveAppendFile("notes.md", "one\n");
        Assert.Equal("one\n", workspace.GetFile("output/notes.md").Content);
        workspace.SaveAppendFile("notes.md", "two\n", append: true);
        Assert.Equal("one\ntwo\n", workspace.GetFile("output/notes.md").Content);

        Assert.Equal("startLine is beyond the end of the file (2 lines).", workspace.GetFile("index.md", 9).Error);
        Assert.Equal("startLine and lineCount must be positive when given.", workspace.GetFile("index.md", 0).Error);
    }

    [Fact]
    public void LongFilesAreTruncatedWithTheNextLine()
    {
        var workspace = Workspace();
        workspace.SetOutput("long.txt", string.Concat(Enumerable.Range(1, 2000).Select(line => line + "\n")));
        var first = workspace.GetFile("output/long.txt");
        Assert.Equal((1, 1500, true, (int?)1501), (first.StartLine, first.EndLine, first.Truncated, first.NextStartLine));
        var rest = workspace.GetFile("output/long.txt", 1501);
        Assert.Equal((1501, 2000, false, (int?)null), (rest.StartLine, rest.EndLine, rest.Truncated, rest.NextStartLine));
    }

    [Theory]
    [InlineData("../references/docs/api-guide.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("examples\\basics\\02-two-step-approval.json")]
    [InlineData("./node-reference.md")]
    [InlineData("missing.md")]
    [InlineData("references/docs/api-guide.md")]
    [InlineData("output/../index.md")]
    public void RejectsPathsOutsideTheDocsAndOutputWithAHint(string path)
    {
        var result = Workspace().GetFile(path);
        Assert.False(result.Success);
        Assert.Equal("Unknown file. Use a relative path exactly as listed by get_files_list, for example examples/gateways/02-parallel-fork-and-join.json or output/workflow.json.", result.Error);
    }

    [Fact]
    public void SavesAppendsReplacesAndEditsOnlyInsideOutputWithinTheSizeCap()
    {
        var workspace = Workspace();
        Assert.Equal("Save only to a relative file name inside output, for example workflow.json.", workspace.SaveAppendFile("../x.md", "x").Error);
        Assert.Equal("Save only to a relative file name inside output, for example workflow.json.", workspace.SaveAppendFile("output/", "x").Error);
        Assert.Equal(new WorkflowAiSaveResult(true, "output/workflow.json", 5, 3, false), workspace.SaveAppendFile("workflow.json", "a\nb\nc"));
        Assert.Equal(new WorkflowAiSaveResult(true, "output/workflow.json", 7, 3, true), workspace.SaveAppendFile("workflow.json", "de", append: true));
        Assert.Equal("a\nb\ncde", workspace.Outputs["workflow.json"]);

        var edit = workspace.EditFile("output/workflow.json", "b\ncde", "B\nCDE");
        Assert.Equal(new WorkflowAiEditResult(true, "output/workflow.json", 7, 3, 1, 2), edit);
        Assert.Equal("a\nB\nCDE", workspace.Outputs["workflow.json"]);
        Assert.Equal("oldText was not found in output/workflow.json. Read the current text with get_file and copy it exactly, including spaces and line breaks.",
            workspace.EditFile("workflow.json", "missing", "x").Error);
        workspace.SaveAppendFile("twice.md", "x x");
        Assert.Equal("oldText occurs 2 times in output/twice.md. Include more surrounding text so it matches exactly once, or set replaceAll to true.",
            workspace.EditFile("twice.md", "x", "y").Error);
        Assert.Equal(2, workspace.EditFile("twice.md", "x", "y", replaceAll: true).Replacements);
        Assert.Equal("oldText must not be empty.", workspace.EditFile("twice.md", "", "y").Error);
        Assert.Equal("oldText and newText are identical, so nothing would change.", workspace.EditFile("twice.md", "y", "y").Error);
        Assert.Equal("Edit only a file you saved, by its name inside output, for example workflow.json. Use get_files_list with folder output to see your saved files.",
            workspace.EditFile("index.md", "#", "*").Error);
        Assert.Equal("# index.md\nLine two.\n", workspace.GetFile("index.md").Content);

        var big = new string('x', (int)WorkflowAiWorkspace.MaxOutputBytes);
        Assert.Equal($"Saving would exceed the {WorkflowAiWorkspace.MaxOutputBytes} byte output limit.", workspace.SaveAppendFile("big.txt", big).Error);
        Assert.Equal($"Editing would exceed the {WorkflowAiWorkspace.MaxOutputBytes} byte output limit.", workspace.EditFile("twice.md", "y", big, replaceAll: true).Error);
        Assert.False(workspace.Outputs.ContainsKey("big.txt"));
    }

    [Fact]
    public void ValidationToolsReportFlowbitsChecksWithLocations()
    {
        var activity = new List<WorkflowAiToolActivity>();
        var workspace = Workspace(activity);
        var none = workspace.ValidateWorkflow();
        Assert.Equal((false, "No output/workflow.json has been saved yet. Save the workflow with save_append_file first."), (none.Success, none.Error));

        var broken = ExampleWorkflowData.Read("examples/basics/02-two-step-approval.json").Replace("\"targetRef\": 2,", "\"targetRef\": 9999,", StringComparison.Ordinal);
        workspace.SaveAppendFile("workflow.json", broken);
        var result = workspace.ValidateWorkflow();
        Assert.True(result.Success);
        Assert.False(result.Valid);
        var error = result.Errors.First(item => item.Code == "definition-rule");
        Assert.Equal(("/sequenceFlows/0", "flow 101 (1 -> 9999)"), (error.Path, error.Where));
        Assert.Equal(WorkflowAiWorkflowCheck.NotChecked, result.NotChecked);

        workspace.SaveAppendFile("workflow.json", ExampleWorkflowData.Read("examples/basics/02-two-step-approval.json"));
        Assert.True(workspace.ValidateWorkflow().Valid);

        var items = workspace.ValidateItem("lane", "[{\"id\":1,\"name\":\"A\"},{\"id\":\"2\"}]");
        Assert.Equal(new[] { "/1", "/1/id" }, items.Errors.Select(item => item.Path));
        Assert.All(items.Errors, item => Assert.StartsWith("item 1: lane", item.Where));
        Assert.Equal(WorkflowAiWorkflowCheck.ItemNotChecked, items.NotChecked);
        Assert.Equal("Unknown kind \"gate\". Use one of: node, variable, sequenceFlow, lane.", workspace.ValidateItem("gate", "{}").Error);

        // Progress names the file and the outcome, never file contents.
        Assert.Equal("Nothing to check yet", activity[0].Summary);
        Assert.Matches(@"^Saved workflow\.json \(\d+ KB\)$", activity[1].Summary);
        Assert.Equal($"Checked workflow.json: {result.ErrorCount} errors", activity[2].Summary);
        Assert.Equal("Checked workflow.json: no errors", activity[4].Summary);
        Assert.Equal((3, 2, 2), (workspace.CallCounts["validate_workflow"], workspace.CallCounts["save_append_file"], workspace.CallCounts["validate_item"]));
    }

    [Fact]
    public void ToolResultsSerializeLikeTheLabsWithNullsOmitted()
    {
        var workspace = Workspace();
        var json = JsonSerializer.Serialize(workspace.GetFile("index.md"), Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions);
        Assert.StartsWith("{", json);
        Assert.Contains("\"totalLines\"", json);
        Assert.DoesNotContain("\"notice\"", json);
        Assert.DoesNotContain("\"error\"", json);
    }
}
