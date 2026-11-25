extern alias FlowbitUi;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Jint;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowEditor = FlowbitUi::Flowbit.Ui.Components.Pages.WorkflowEditor;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The hosted editor's signal catalog capability (EVENT-04F): the adapter asks Blazor and answers
/// <c>unavailable</c> instead of failing, and the two Blazor methods read the catalog with the page's
/// credentials and reply <c>ok</c>, <c>missing</c>, <c>forbidden</c>, or <c>unavailable</c>.
/// </summary>
public sealed class WorkflowEditorHostSignalCatalogTests
{
    [Fact]
    public async Task SignalCatalogAsksBlazorAndReportsUnavailableInsteadOfFailing()
    {
        using var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("""
            let mounted = null;
            const document = { body: {}, createElement: () => ({ remove() {} }), head: { append: element => element.onload() } };
            const MutationObserver = class { observe() {} disconnect() {} };
            const window = { FlowbitEditor: { mount: (_root, options) => { mounted = options; return { dispose() {} }; } } };
            """);
        engine.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-editor-host.js"))
            .Replace("export async function create", "async function create", StringComparison.Ordinal)
            .Replace("export async function createViewer", "async function createViewer", StringComparison.Ordinal));
        var result = await engine.EvaluateAsync("""
            (async () => {
              const calls = [];
              let fail = false;
              const dotnet = {
                invokeMethodAsync: (method, ...args) => {
                  calls.push({ method, args });
                  return fail ? Promise.reject(new Error('Circuit lost.')) : Promise.resolve({ status: 'ok', method });
                }
              };
              const host = await create({ isConnected: true }, dotnet, { template: 'template', stylesheet: 'css', script: 'script' });
              const entries = await mounted.signalCatalog.entries();
              const entry = await mounted.signalCatalog.entry('claims.policy-changed');
              fail = true;
              const failedEntries = await mounted.signalCatalog.entries();
              const failedEntry = await mounted.signalCatalog.entry('claims.policy-changed');
              host.dispose();
              const callsBeforeClosed = calls.length;
              const closedEntries = await mounted.signalCatalog.entries();
              const closedEntry = await mounted.signalCatalog.entry('claims.policy-changed');
              return JSON.stringify({
                calls, entries, entry, failedEntries, failedEntry, closedEntries, closedEntry,
                callsBeforeClosed, callsAfterClosed: calls.length
              });
            })()
            """);
        using var json = JsonDocument.Parse(result.AsString());
        var root = json.RootElement;

        var calls = root.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal("GetSignalCatalog", calls[0].GetProperty("method").GetString());
        Assert.Equal("[]", calls[0].GetProperty("args").GetRawText());
        Assert.Equal("GetSignalCatalogEntry", calls[1].GetProperty("method").GetString());
        Assert.Equal("""["claims.policy-changed"]""", calls[1].GetProperty("args").GetRawText());
        Assert.Equal("GetSignalCatalog", root.GetProperty("entries").GetProperty("method").GetString());
        Assert.Equal("GetSignalCatalogEntry", root.GetProperty("entry").GetProperty("method").GetString());
        foreach (var name in new[] { "failedEntries", "failedEntry", "closedEntries", "closedEntry" })
        {
            Assert.Equal("""{"status":"unavailable"}""", root.GetProperty(name).GetRawText());
        }
        Assert.Equal(root.GetProperty("callsBeforeClosed").GetInt32(), root.GetProperty("callsAfterClosed").GetInt32());
    }

    [Fact]
    public async Task GetSignalCatalog_ReadsActiveEntriesInPagesOf200UpTo1000AndShortensDescriptions()
    {
        // 1,205 active entries: five pages are read and the reply says that more exist.
        var catalog = Enumerable.Range(0, 1205).Select(index => Entry($"catalog.entry-{index:D4}")).ToList();
        // A description cut at 120 text elements keeps the emoji and the combining mark whole.
        catalog[0] = Entry("catalog.entry-0000", new string('a', 119) + "\U0001F600" + "rest of the description",
            fields: [new SignalCatalogFieldDto("reason", "string", Required: true), new SignalCatalogFieldDto("skus", "string", IsArray: true)]);
        catalog[1] = Entry("catalog.entry-0001", new string('b', 119) + "e\u0301" + "tail");
        catalog[2] = Entry("catalog.entry-0002", "Short.", allowWorkflows: false);
        catalog[3] = Entry("catalog.entry-0003", description: null);
        var handler = new CatalogHandler(catalog, mode: "required");
        var editor = Editor(handler);

        var reply = await editor.GetSignalCatalog();

        Assert.Equal("ok", reply.Status);
        Assert.Equal("required", reply.Mode);
        Assert.True(reply.Truncated);
        Assert.Equal(1000, reply.Entries!.Count);
        Assert.Equal(
            Enumerable.Range(1, 5).Select(page =>
                $"/api/signal-catalog/entries?page={page}&pageSize=200&status=active"),
            handler.Paths);
        var first = reply.Entries[0];
        Assert.Equal("catalog.entry-0000", first.Name);
        Assert.Equal(new string('a', 119) + "\U0001F600", first.Description);
        Assert.Equal(2, first.FieldCount);
        Assert.True(first.AllowWorkflows);
        Assert.Equal(new string('b', 119) + "e\u0301", reply.Entries[1].Description);
        Assert.Equal(120, new StringInfo(reply.Entries[1].Description!).LengthInTextElements);
        Assert.Equal("Short.", reply.Entries[2].Description);
        Assert.False(reply.Entries[2].AllowWorkflows);
        Assert.Null(reply.Entries[3].Description);
        Assert.Equal("catalog.entry-0999", reply.Entries[^1].Name);

        // A full catalog of the longest names and descriptions carries no fields and no long text.
        var summaries = Enumerable.Range(0, 1000).Select(index => Entry(
            new string('x', 90) + $".{index:D4}-name", new string('d', 1000),
            fields: Enumerable.Range(0, 20).Select(field => new SignalCatalogFieldDto($"field{field}", "json")).ToList())).ToList();
        Assert.All(summaries, item => Assert.Equal(100, item.Name.Length));
        var full = await Editor(new CatalogHandler(summaries, mode: "optional")).GetSignalCatalog();
        Assert.False(full.Truncated);
        Assert.Equal(1000, full.Entries!.Count);
        Assert.All(full.Entries, item => Assert.Equal(20, item.FieldCount));
        var size = JsonSerializer.SerializeToUtf8Bytes(full, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        Assert.InRange(size, 1, 300_000);
    }

    [Fact]
    public async Task GetSignalCatalog_ReadsOnePageForASmallCatalogAndAnswersEachFailure()
    {
        var small = new CatalogHandler([Entry("claims.policy-changed", "A policy changed.")], mode: "optional");
        var reply = await Editor(small).GetSignalCatalog();
        Assert.Equal("ok", reply.Status);
        Assert.False(reply.Truncated);
        Assert.Equal("optional", reply.Mode);
        Assert.Single(small.Paths);
        Assert.Equal([new SignalCatalogEntrySummaryDto("claims.policy-changed", "A policy changed.", true, 0)], reply.Entries!);

        Assert.Equal("forbidden", (await Editor(new StatusHandler(HttpStatusCode.Forbidden)).GetSignalCatalog()).Status);
        Assert.Equal("forbidden", (await Editor(new StatusHandler(HttpStatusCode.Unauthorized)).GetSignalCatalog()).Status);
        Assert.Equal("unavailable", (await Editor(new StatusHandler(HttpStatusCode.InternalServerError)).GetSignalCatalog()).Status);
        Assert.Equal("unavailable", (await Editor(new ThrowingHandler()).GetSignalCatalog()).Status);

        // A reply read under another identity is not shown.
        var token = new TokenState();
        token.Set("first-token");
        var changing = new CatalogHandler([Entry("claims.policy-changed")], mode: "optional") { OnRequest = () => token.Set("second-token") };
        Assert.Equal("forbidden", (await Editor(changing, token).GetSignalCatalog()).Status);

        // Without an identity, and after disposal, nothing is requested.
        var signedOut = new TokenState();
        signedOut.ConfigureOidcMode();
        var unused = new StatusHandler(HttpStatusCode.OK);
        Assert.Equal("forbidden", (await Editor(unused, signedOut).GetSignalCatalog()).Status);
        var disposed = Editor(unused);
        await disposed.DisposeAsync();
        Assert.Equal("unavailable", (await disposed.GetSignalCatalog()).Status);
        Assert.Equal(0, unused.Requests);
    }

    [Fact]
    public async Task GetSignalCatalogEntry_RepliesWithTheEntryMissingOrAFailure()
    {
        var entry = Entry("warehouse.product-recall", "A supplier recalled a product.",
            fields: [new SignalCatalogFieldDto("reason", "string", Required: true, Description: "Why.")],
            status: "archived");
        var handler = new CatalogHandler([entry], mode: "optional");

        var found = await Editor(handler).GetSignalCatalogEntry("warehouse.product-recall");
        Assert.Equal("ok", found.Status);
        Assert.Equal("warehouse.product-recall", found.Entry!.Name);
        Assert.Equal("archived", found.Entry.Status);
        Assert.Equal([new SignalCatalogFieldDto("reason", "string", false, true, "Why.")], found.Entry.Fields);

        var missing = await Editor(handler).GetSignalCatalogEntry("claims.unknown");
        Assert.Equal("missing", missing.Status);
        Assert.Null(missing.Entry);
        Assert.Equal(["/api/signal-catalog/entries/warehouse.product-recall", "/api/signal-catalog/entries/claims.unknown"],
            handler.Paths);

        // A name the catalog cannot hold is never sent.
        var unused = new StatusHandler(HttpStatusCode.OK);
        Assert.Equal("missing", (await Editor(unused).GetSignalCatalogEntry("Claims/../settings")).Status);
        Assert.Equal("missing", (await Editor(unused).GetSignalCatalogEntry("")).Status);
        Assert.Equal(0, unused.Requests);

        Assert.Equal("forbidden", (await Editor(new StatusHandler(HttpStatusCode.Forbidden)).GetSignalCatalogEntry("claims.a")).Status);
        Assert.Equal("forbidden", (await Editor(new StatusHandler(HttpStatusCode.Unauthorized)).GetSignalCatalogEntry("claims.a")).Status);
        Assert.Equal("unavailable", (await Editor(new StatusHandler(HttpStatusCode.InternalServerError)).GetSignalCatalogEntry("claims.a")).Status);
        Assert.Equal("unavailable", (await Editor(new ThrowingHandler()).GetSignalCatalogEntry("claims.a")).Status);

        var token = new TokenState();
        token.Set("first-token");
        var changing = new CatalogHandler([entry], mode: "optional") { OnRequest = () => token.Set("second-token") };
        Assert.Equal("forbidden", (await Editor(changing, token).GetSignalCatalogEntry("warehouse.product-recall")).Status);

        var disposed = Editor(unused);
        await disposed.DisposeAsync();
        Assert.Equal("unavailable", (await disposed.GetSignalCatalogEntry("claims.a")).Status);
        Assert.Equal(0, unused.Requests);
    }

    private static WorkflowEditor Editor(HttpMessageHandler handler, TokenState? token = null)
    {
        var editor = new WorkflowEditor();
        if (token is null)
        {
            token = new TokenState();
            token.Set("editor-token");
        }
        SetProperty(editor, "Token", token);
        SetProperty(editor, "Api", new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") }));
        return editor;
    }

    private static void SetProperty(WorkflowEditor editor, string name, object value) =>
        typeof(WorkflowEditor).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);

    private static SignalCatalogEntryDto Entry(
        string name,
        string? description = "An entry.",
        IReadOnlyList<SignalCatalogFieldDto>? fields = null,
        bool allowWorkflows = true,
        string status = "active") =>
        new(name, description, status, fields ?? [], [], true, allowWorkflows, true,
            DateTimeOffset.Parse("2026-11-24T09:00:00Z", CultureInfo.InvariantCulture), "alice",
            DateTimeOffset.Parse("2026-11-24T09:00:00Z", CultureInfo.InvariantCulture), "alice",
            status == "archived" ? DateTimeOffset.Parse("2026-11-24T10:00:00Z", CultureInfo.InvariantCulture) : null);

    /// <summary>Answers the list and entry routes from an in-memory catalog, ordered as given.</summary>
    private sealed class CatalogHandler(IReadOnlyList<SignalCatalogEntryDto> catalog, string mode) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public Action? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            OnRequest?.Invoke();
            const string entryPrefix = "/api/signal-catalog/entries/";
            if (path.StartsWith(entryPrefix, StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path[entryPrefix.Length..]);
                var entry = catalog.FirstOrDefault(item => item.Name == name);
                return Task.FromResult(entry is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { error = "Not found." }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(entry) });
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var page = int.Parse(query["page"]!, CultureInfo.InvariantCulture);
            var size = int.Parse(query["pageSize"]!, CultureInfo.InvariantCulture);
            var items = catalog.Skip((page - 1) * size).Take(size).ToList();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SignalCatalogListDto(items, page, size, catalog.Count, mode, ["signal-sender"]))
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = "Refused." }) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The API could not be reached.");
    }
}
