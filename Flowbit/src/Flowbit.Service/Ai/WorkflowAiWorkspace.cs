using System.ComponentModel;
using System.Text;
using Flowbit.Service.Authoring;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

public sealed record WorkflowAiFileEntry(string Path, long Bytes, int Lines);
public sealed record WorkflowAiFilesListResult(bool Success, string Folder, IReadOnlyList<WorkflowAiFileEntry> Files, string? Error = null);
public sealed record WorkflowAiFileContentResult(bool Success, string Path, int TotalLines, int StartLine, int EndLine, bool Truncated,
    int? NextStartLine, string? Content, string? Notice = null, string? Error = null);
public sealed record WorkflowAiSaveResult(bool Success, string Path, long Bytes, int Lines, bool Appended, string? Error = null);
public sealed record WorkflowAiEditResult(bool Success, string Path, long Bytes, int Lines, int Replacements, int? FirstChangedLine, string? Error = null);
/// <summary>One validation error: its code, JSON path, the node/flow/variable/lane it is in, and what is wrong.</summary>
public sealed record WorkflowAiToolError(string Code, string Path, string? Where, string Message);
/// <summary>Success is false only when the call itself cannot be checked; Valid says whether the checked JSON passes.</summary>
public sealed record WorkflowAiValidationToolResult(bool Success, bool Valid, int ErrorCount, IReadOnlyList<WorkflowAiToolError> Errors,
    IReadOnlyList<string> NotChecked, string? Error = null);

/// <summary>One tool call, for progress and metadata-only telemetry. The summary names a docs or output path, never file contents.</summary>
public sealed record WorkflowAiToolActivity(string Tool, bool Success, string Summary);

/// <summary>
/// The only tools the authoring agent receives, ported from Flowbit AI Lab v3 (Tools/FileTools.cs) with the same names, descriptions,
/// limits and results: list and read the docs (read-only) or the agent's own output, save into output/, replace exact text in a saved
/// file, and check the saved workflow or single items with Flowbit's checks. Files live in memory; nothing touches the disk.
/// </summary>
public sealed class WorkflowAiWorkspace
{
    public const string OutputPrefix = "output/";
    public const int MaxLinesPerRead = 1500;
    public const int MaxCharactersPerRead = 120_000;
    public const long MaxOutputBytes = 2 * 1024 * 1024;
    public const int MaxErrorsPerResult = 40;
    private const string PathHint = "Use a relative path exactly as listed by get_files_list, for example examples/gateways/02-parallel-fork-and-join.json or output/workflow.json.";

    private readonly WorkflowAiDocs docs;
    private readonly SortedDictionary<string, string> outputs;
    private readonly WorkflowAiWorkflowCheck check;
    private readonly Action<WorkflowAiToolActivity>? observe;
    // Reads whose exact content is already in the conversation, restored with the conversation state.
    private readonly HashSet<string> returned;
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

    public WorkflowAiWorkspace(WorkflowAiDocs docs, IEnumerable<KeyValuePair<string, string>>? outputs, IEnumerable<string>? previouslyReturned,
        WorkflowAiWorkflowCheck check, Action<WorkflowAiToolActivity>? observe = null)
    {
        this.docs = docs;
        this.outputs = new(outputs?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) ?? [], StringComparer.Ordinal);
        returned = new(previouslyReturned ?? [], StringComparer.Ordinal);
        this.check = check;
        this.observe = observe;
    }

    public IReadOnlyDictionary<string, string> Outputs => outputs;
    public IReadOnlyList<string> ReturnedReads => returned.Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyDictionary<string, int> CallCounts => counts;

    /// <summary>Replaces or removes a saved file outside a tool call, such as the editor's current diagram.</summary>
    public void SetOutput(string name, string? text)
    {
        if (text is null) outputs.Remove(name);
        else outputs[name] = text;
    }

    public IList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(GetFilesList, "get_files_list"),
        AIFunctionFactory.Create(GetFile, "get_file"),
        AIFunctionFactory.Create(SaveAppendFile, "save_append_file"),
        AIFunctionFactory.Create(EditFile, "edit_file"),
        AIFunctionFactory.Create(ValidateWorkflow, "validate_workflow"),
        AIFunctionFactory.Create(ValidateItem, "validate_item")
    ];

    [Description("List files with their size and line count. Omit folder to list everything: the Flowbit documentation (.md) and example files plus the files you saved, which are listed under output/. Give a subfolder such as examples/gateways, or output for only your saved files.")]
    public WorkflowAiFilesListResult GetFilesList([Description("Optional subfolder, or output for only your saved files.")] string? folder = null)
    {
        WorkflowAiFilesListResult result;
        var selected = folder?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(selected))
            result = new(true, "", List(docs.Files, "", "").Concat(List(outputs, "", OutputPrefix)).ToArray());
        else if (selected == "output")
            result = new(true, "output", List(outputs, "", OutputPrefix).ToArray());
        else if (ValidRelative(selected) && docs.Files.Keys.Any(path => path.StartsWith(selected + "/", StringComparison.Ordinal)))
            result = new(true, selected, List(docs.Files, selected + "/", "").ToArray());
        else
            result = new(false, folder ?? "", [], "Unknown folder. " + PathHint);
        Report("get_files_list", result.Success, result.Success ? (selected is { Length: > 0 } ? "Listed " + selected : "Listed the files") : "Listed an unknown folder");
        return result;
    }

    [Description("Read a documentation, example or saved output file (paths starting with output/). Without startLine/lineCount it returns the whole file up to 1500 lines; a longer file is marked truncated with the nextStartLine to continue from.")]
    public WorkflowAiFileContentResult GetFile([Description("Relative path from get_files_list, e.g. node-reference.md or output/workflow.json.")] string path,
        [Description("Optional first line to return (1-based).")] int? startLine = null,
        [Description("Optional number of lines to return (at most 1500).")] int? lineCount = null)
    {
        WorkflowAiFileContentResult result;
        var trimmed = path?.Trim() ?? "";
        var isOutput = trimmed.StartsWith(OutputPrefix, StringComparison.Ordinal);
        var relative = isOutput ? trimmed[OutputPrefix.Length..] : trimmed;
        string? text = null;
        var found = ValidRelative(relative) && (isOutput ? outputs.TryGetValue(relative, out text) : docs.Files.TryGetValue(relative, out text));
        if (!found || text is null) result = Failure(path ?? "", "Unknown file. " + PathHint);
        else if (startLine is < 1 || lineCount is < 1) result = Failure(path ?? "", "startLine and lineCount must be positive when given.");
        else
        {
            var lines = SplitLines(text);
            var start = startLine ?? 1;
            if (start > Math.Max(lines.Length, 1)) result = Failure(path ?? "", $"startLine is beyond the end of the file ({lines.Length} lines).");
            else
            {
                var limit = Math.Min(lineCount ?? MaxLinesPerRead, MaxLinesPerRead);
                var builder = new StringBuilder();
                var end = start - 1;
                while (end < lines.Length && end - start + 1 < limit && builder.Length + lines[end].Length <= MaxCharactersPerRead)
                    builder.Append(lines[end++]);
                if (end == start - 1 && end < lines.Length) builder.Append(lines[end++]);
                var truncated = end < lines.Length && lineCount is null;
                var content = builder.ToString();
                var display = (isOutput ? OutputPrefix : "") + relative;
                var key = $"{display}|{start}|{end}|{AuthoringPackageBuilder.Hash(content)}";
                result = returned.Add(key)
                    ? new(true, display, lines.Length, start, end, truncated, end < lines.Length ? end + 1 : null, content)
                    : new(true, display, lines.Length, start, end, truncated, end < lines.Length ? end + 1 : null, null,
                        "This exact content was already returned earlier in this conversation; use that earlier result instead of reading it again.");
            }
        }
        Report("get_file", result.Success, result.Success
            ? $"Read {result.Path}{(result.StartLine > 1 || result.EndLine < result.TotalLines ? $" (lines {result.StartLine}-{result.EndLine})" : "")}"
            : "Asked for an unknown file");
        return result;
    }

    [Description("Save text into your output folder. append=false creates or replaces the file; append=true adds the content to the end, so a large file can be written in parts.")]
    public WorkflowAiSaveResult SaveAppendFile([Description("File name inside output, e.g. workflow.json or open-questions.md.")] string path,
        [Description("Text to write.")] string content,
        [Description("True to append to the existing file, false to create or replace it.")] bool append = false)
    {
        WorkflowAiSaveResult result;
        var relative = OutputName(path);
        content ??= "";
        if (relative is null || relative.EndsWith('/'))
            result = new(false, path ?? "", 0, 0, append, "Save only to a relative file name inside output, for example workflow.json.");
        else
        {
            var existing = outputs.TryGetValue(relative, out var current) ? Encoding.UTF8.GetByteCount(current) : 0;
            var total = outputs.Values.Sum(text => (long)Encoding.UTF8.GetByteCount(text)) - (append ? 0 : existing) + Encoding.UTF8.GetByteCount(content);
            if (total > MaxOutputBytes) result = new(false, path ?? "", existing, 0, append, $"Saving would exceed the {MaxOutputBytes} byte output limit.");
            else
            {
                var text = append && current is not null ? current + content : content;
                outputs[relative] = text;
                result = new(true, OutputPrefix + relative, Encoding.UTF8.GetByteCount(text), SplitLines(text).Length, append);
            }
        }
        Report("save_append_file", result.Success, result.Success
            ? $"{(append ? "Appended to" : "Saved")} {relative} ({Size(result.Bytes)})" : $"Could not save {path}");
        return result;
    }

    [Description("Replace exact text in a file you saved under output/, such as workflow.json or open-questions.md. oldText must match the current file text exactly, including spaces and line breaks, and must occur once unless replaceAll is true. Use it to correct part of a saved file instead of saving the whole file again.")]
    public WorkflowAiEditResult EditFile([Description("File name inside output, e.g. workflow.json or output/workflow.json.")] string path,
        [Description("The exact current text to replace.")] string oldText,
        [Description("The replacement text; empty to delete oldText.")] string newText,
        [Description("True to replace every occurrence; false requires exactly one.")] bool replaceAll = false)
    {
        WorkflowAiEditResult result;
        var relative = OutputName(path);
        newText ??= "";
        if (relative is null || !outputs.TryGetValue(relative, out var text))
            result = EditFailure(path, "Edit only a file you saved, by its name inside output, for example workflow.json. Use get_files_list with folder output to see your saved files.");
        else if (string.IsNullOrEmpty(oldText)) result = EditFailure(path, "oldText must not be empty.");
        else if (oldText == newText) result = EditFailure(path, "oldText and newText are identical, so nothing would change.");
        else
        {
            var display = OutputPrefix + relative;
            var first = text.IndexOf(oldText, StringComparison.Ordinal);
            var count = 0;
            for (var at = first; at >= 0; at = text.IndexOf(oldText, at + oldText.Length, StringComparison.Ordinal)) count++;
            if (count == 0)
                result = EditFailure(path, $"oldText was not found in {display}. Read the current text with get_file and copy it exactly, including spaces and line breaks.");
            else if (count > 1 && !replaceAll)
                result = EditFailure(path, $"oldText occurs {count} times in {display}. Include more surrounding text so it matches exactly once, or set replaceAll to true.");
            else
            {
                var updated = text.Replace(oldText, newText, StringComparison.Ordinal);
                var total = outputs.Values.Sum(item => (long)Encoding.UTF8.GetByteCount(item)) - Encoding.UTF8.GetByteCount(text) + Encoding.UTF8.GetByteCount(updated);
                if (total > MaxOutputBytes) result = EditFailure(path, $"Editing would exceed the {MaxOutputBytes} byte output limit.");
                else
                {
                    outputs[relative] = updated;
                    result = new(true, display, Encoding.UTF8.GetByteCount(updated), SplitLines(updated).Length, count, text.AsSpan(0, first).Count('\n') + 1);
                }
            }
        }
        Report("edit_file", result.Success, result.Success ? $"Edited {relative}" : $"An edit of {relative ?? path} did not apply");
        return result;
    }

    [Description("Check the saved output/workflow.json with the Flowbit checks (schema, references, topology, routing, configuration, expression syntax). Each error gives its code, JSON path, the node, flow, variable or lane it is in, and what is wrong. Passing does not check business correctness.")]
    public WorkflowAiValidationToolResult ValidateWorkflow()
    {
        var result = !outputs.TryGetValue(WorkflowAiAgent.WorkflowFile, out var text)
            ? Refused($"No {OutputPrefix}{WorkflowAiAgent.WorkflowFile} has been saved yet. Save the workflow with save_append_file first.", WorkflowAiWorkflowCheck.NotChecked)
            : Result(check.Check(text).Errors, WorkflowAiWorkflowCheck.NotChecked);
        Report("validate_workflow", result.Success, !result.Success ? "Nothing to check yet"
            : result.Valid ? "Checked workflow.json: no errors" : $"Checked workflow.json: {Errors(result.ErrorCount)}");
        return result;
    }

    [Description("Check one workflow item, or a JSON array of items of the same kind, against its Flowbit schema, for example before saving it. kind is node, variable, sequenceFlow or lane. Each error gives its JSON path, the item it is in, and what is wrong. Only the item's own structure is checked; use validate_workflow for the saved file.")]
    public WorkflowAiValidationToolResult ValidateItem([Description("node, variable, sequenceFlow or lane.")] string kind,
        [Description("The item as JSON text, or a JSON array of items of this kind.")] string content)
    {
        var checkedItems = WorkflowAiWorkflowCheck.CheckItems(kind ?? "", content ?? "");
        var result = checkedItems.Refusal is { } refusal
            ? Refused(refusal, WorkflowAiWorkflowCheck.ItemNotChecked) : Result(checkedItems.Errors, WorkflowAiWorkflowCheck.ItemNotChecked);
        Report("validate_item", result.Success, result.Success ? $"Checked {kind} items: {Errors(result.ErrorCount)}" : "Could not check the items");
        return result;
    }

    private static WorkflowAiValidationToolResult Result(IReadOnlyList<WorkflowAiError> errors, IReadOnlyList<string> notChecked) =>
        new(true, errors.Count == 0, errors.Count, errors.Take(MaxErrorsPerResult)
            .Select(error => new WorkflowAiToolError(error.Code, error.Path, error.Where.Length == 0 ? null : error.Where, error.Message)).ToArray(), notChecked);

    private static WorkflowAiValidationToolResult Refused(string error, IReadOnlyList<string> notChecked) => new(false, false, 0, [], notChecked, error);

    private static IEnumerable<WorkflowAiFileEntry> List(IEnumerable<KeyValuePair<string, string>> files, string prefix, string display) => files
        .Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal))
        .Select(file => new WorkflowAiFileEntry(display + file.Key, Encoding.UTF8.GetByteCount(file.Value), SplitLines(file.Value).Length))
        .OrderBy(entry => entry.Path, StringComparer.Ordinal);

    private static string? OutputName(string? path)
    {
        var relative = path?.Trim() ?? "";
        if (relative.StartsWith(OutputPrefix, StringComparison.Ordinal)) relative = relative[OutputPrefix.Length..];
        return ValidRelative(relative.TrimEnd('/')) ? relative : null;
    }

    // Only plain relative paths; no backslashes, drive letters, '.' or '..' segments.
    private static bool ValidRelative(string relative) =>
        !string.IsNullOrWhiteSpace(relative) && !relative.Contains('\\') && !relative.Contains(':') && !relative.StartsWith('/')
        && !Path.IsPathRooted(relative) && !relative.Split('/').Any(segment => segment is "" or "." or "..");

    // Lines keep their own terminators so returned content is the exact file text.
    internal static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return lines.ToArray();
    }

    private static WorkflowAiFileContentResult Failure(string path, string error) => new(false, path, 0, 0, 0, false, null, null, null, error);
    private static WorkflowAiEditResult EditFailure(string? path, string error) => new(false, path ?? "", 0, 0, 0, null, error);
    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} bytes" : $"{(bytes + 512) / 1024} KB";
    private static string Errors(int count) => count == 1 ? "1 error" : $"{count} errors";

    private void Report(string tool, bool success, string summary)
    {
        counts[tool] = counts.GetValueOrDefault(tool) + 1;
        observe?.Invoke(new(tool, success, summary));
    }
}
