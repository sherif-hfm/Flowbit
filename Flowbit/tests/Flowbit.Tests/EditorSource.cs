using System.Text.RegularExpressions;

namespace Flowbit.Tests;

/// <summary>Characterizes the actual shared template/styles and instance body, never a copied editor.</summary>
internal static class EditorSource
{
    /// <param name="mode">
    /// The mount mode the instance body sees. <c>viewer</c> also records each
    /// selection the viewer reports in <c>viewerSelections</c>.
    /// </param>
    public static string Read(string mode = "standalone")
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "editor");
        var templateScript = File.ReadAllText(Path.Combine(directory, "editor-template.js"));
        var markup = templateScript[(templateScript.IndexOf('`') + 1)..templateScript.LastIndexOf('`')];
        var script = File.ReadAllText(Path.Combine(directory, "flowbit-editor.js"));
        var body = Regex.Match(script, @"// BEGIN EDITOR INSTANCE(?<body>[\s\S]*?)// END EDITOR INSTANCE").Groups["body"].Value;
        var options = mode == "viewer"
            ? "const viewerSelections = [];\nconst options = { mode: 'viewer', label: 'Test diagram', onSelectionChange: id => viewerSelections.push(id) };"
            : $"const options = {{ mode: '{mode}' }};";
        // Existing Jint characterizations inspect private helpers. Unwrap the real
        // instance body for those tests; real-browser tests exercise mount/dispose.
        const string scope = """
            const root = Object.assign({}, document.body, {
              dataset: document.documentElement.dataset,
              querySelector: selector => document.querySelector(selector === '.editor-workspace' ? 'main' : selector),
              querySelectorAll: selector => document.querySelectorAll(selector),
              getBoundingClientRect: () => ({ width: window.innerWidth, right: window.innerWidth }),
              addEventListener: (...args) => document.addEventListener(...args)
            });
            const byId = id => document.getElementById(id);
            const listen = (target, ...args) => target.addEventListener(...args);
            const cleanup = [];
            const readers = new Set();
            const disposed = false;
            const hostBridge = null;
            const hasEditorFocus = () => true;
            """;
        return ("<head><script>" + File.ReadAllText(Path.Combine(directory, "theme-init.js")) + "</script>" +
            "<style>" + File.ReadAllText(Path.Combine(directory, "flowbit-editor.css")) + "</style></head>" +
            markup + "<script>" + options + "\n" + scope + body + "</script>").Replace("\r\n", "\n");
    }
}
