using System.Text.Json;
using Jint;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The hosted editor's signal catalog (EVENT-04F): with the optional <c>signalCatalog</c> capability the name field
/// suggests cataloged names, a status block under it shows the name's entry or one note, and Add missing fields
/// adds payload entries or mapping rows as one undoable change. Replies live outside the document, and catalog
/// text is always set as text.
/// </summary>
public sealed class EditorSignalCatalogTests
{
    private const string Markup = "<img src=x onerror=alert(1)>";

    private const string Diagram = """
        // Throw #7 and catch #9 use warehouse.product-recall, whose entry has four fields; the throw already sends
        // 'Reason' and the catch already maps 'reason'. Signal end #12 uses claims.closed, which workflows may not
        // throw; boundary #14 uses the archived claims.archived; catch #16 uses a name without an entry; catch #17
        // uses claims.markup, whose description and field name hold markup. Start #13, end #15, and boundary #18
        // use warehouse.product-recall with nothing mapped or sent. The process variable skus is a number where
        // the field is string[], and Details is json, as the field is.
        const catalogDiagram = () => ({
          id: 'signal-catalog-editor', name: 'Signal catalog editor', initialEventId: 1,
          variables: [
            { id: 1, name: 'reason', dataType: 'string', isArray: false, nullable: true, required: false, defaultValue: null, validation: null },
            { id: 2, name: 'skus', dataType: 'number', isArray: false, nullable: true, required: false, defaultValue: null, validation: null },
            { id: 3, name: 'Details', dataType: 'json', isArray: false, nullable: true, required: false, defaultValue: null, validation: null }
          ],
          lanes: [{ id: 10, name: 'Team', x: 0, y: 0, w: 1600, h: 700 }],
          flowNodes: [
            { id: 1, name: 'Start', type: 'startEvent', laneId: 10, x: 40, y: 100 },
            { id: 2, name: 'Review', type: 'userTask', laneId: 10, x: 160, y: 90 },
            { id: 7, name: 'Announce the recall', type: 'intermediateSignalThrowEvent', laneId: 10, x: 420, y: 100,
              signal: { name: 'warehouse.product-recall', scope: { kind: 'allWorkflows' },
                payload: [{ name: 'Reason', variable: 'reason', required: true }] } },
            { id: 8, name: 'Announced', type: 'endEvent', laneId: 10, x: 620, y: 100 },
            { id: 9, name: 'Wait for the recall', type: 'intermediateSignalCatchEvent', laneId: 10, x: 420, y: 220,
              signal: { name: 'warehouse.product-recall', outputMappings: [
                { variable: 'why', path: 'reason', dataType: 'string', isArray: false, required: true } ] } },
            { id: 10, name: 'Stopped', type: 'endEvent', laneId: 10, x: 620, y: 220 },
            { id: 12, name: 'Close the claims', type: 'signalEndEvent', laneId: 10, x: 420, y: 330,
              signal: { name: 'claims.closed', scope: { kind: 'allWorkflows' } } },
            { id: 13, name: 'Start on a recall', type: 'signalStartEvent', laneId: 10, x: 40, y: 330,
              signal: { name: 'warehouse.product-recall', outputMappings: [] } },
            { id: 14, name: 'Archived', type: 'signalBoundaryEvent', attachedToRef: 2, cancelActivity: true,
              signal: { name: 'claims.archived', outputMappings: [] } },
            { id: 15, name: 'Recall ended', type: 'signalEndEvent', laneId: 10, x: 620, y: 330,
              signal: { name: 'warehouse.product-recall', scope: { kind: 'allWorkflows' }, payload: [] } },
            { id: 16, name: 'Unknown', type: 'intermediateSignalCatchEvent', laneId: 10, x: 420, y: 440,
              signal: { name: 'claims.unknown', outputMappings: [] } },
            { id: 17, name: 'Markup', type: 'intermediateSignalCatchEvent', laneId: 10, x: 420, y: 550,
              signal: { name: 'claims.markup', outputMappings: [] } },
            { id: 18, name: 'Recall on review', type: 'signalBoundaryEvent', attachedToRef: 2, cancelActivity: false,
              signal: { name: 'warehouse.product-recall', outputMappings: [] } }
          ],
          sequenceFlows: [
            { id: 101, name: '', sourceRef: 1, targetRef: 2 },
            { id: 201, name: 'Announce', sourceRef: 2, targetRef: 7 },
            { id: 701, name: '', sourceRef: 7, targetRef: 8 },
            { id: 901, name: '', sourceRef: 9, targetRef: 10 }
          ]
        });
        const markup = '<img src=x onerror=alert(1)>';
        const catalogEntries = {
          'claims.archived': { name: 'claims.archived', description: 'Claims were archived.', status: 'archived',
            allowWorkflows: true, fields: [] },
          'claims.closed': { name: 'claims.closed', description: 'The claims of a policy were closed.', status: 'active',
            allowWorkflows: false, fields: [{ name: 'claimId', dataType: 'number', isArray: false, required: true, description: null }] },
          'claims.empty': { name: 'claims.empty', description: 'Nothing to say.', status: 'active', allowWorkflows: true, fields: [] },
          'claims.internal': { name: 'claims.internal', description: 'Thrown by people only.', status: 'active',
            allowWorkflows: false, fields: [] },
          'claims.markup': { name: 'claims.markup', description: 'Before ' + markup + ' after', status: 'active',
            allowWorkflows: true, fields: [{ name: markup, dataType: 'string', isArray: false, required: false, description: markup }] },
          'warehouse.product-recall': { name: 'warehouse.product-recall',
            description: 'A supplier recalled a product. Open orders for it stop.', status: 'active', allowWorkflows: true,
            fields: [
              { name: 'reason', dataType: 'string', isArray: false, required: true, description: 'Why the product was recalled.' },
              { name: 'skus', dataType: 'string', isArray: true, required: true, description: null },
              { name: 'lot-number', dataType: 'string', isArray: false, required: false, description: null },
              { name: 'details', dataType: 'json', isArray: false, required: false, description: 'Supplier data.' }
            ] }
        };
        // The host capability: every request waits until answerAll answers it, as a Blazor round trip does.
        const calls = [];
        const pending = [];
        const settle = async () => { for (let index = 0; index < 20; index++) await Promise.resolve(); };
        const installCatalog = ({ mode = 'optional', listStatus = 'ok' } = {}) => {
          options.signalCatalog = {
            entries: () => {
              calls.push('entries');
              return new Promise(resolve => pending.push(() => resolve(listStatus !== 'ok' ? { status: listStatus } : {
                status: 'ok', mode, truncated: false,
                entries: Object.values(catalogEntries)
                  .filter(entry => entry.status === 'active')
                  .map(entry => ({ name: entry.name, description: entry.description,
                    allowWorkflows: entry.allowWorkflows, fieldCount: entry.fields.length }))
              })));
            },
            entry: name => {
              calls.push('entry:' + name);
              return new Promise(resolve => pending.push(() => resolve(catalogEntries[name]
                ? { status: 'ok', entry: JSON.parse(JSON.stringify(catalogEntries[name])) }
                : { status: 'missing' })));
            }
          };
        };
        const answerAll = async () => {
          await settle();
          while (pending.length) {
            pending.shift()();
            await settle();
          }
        };
        // Every created element records focus; the stub has no focus or selector engine of its own.
        const created = [];
        let focused = null;
        document.createElement = (create => tag => {
          const element = create(tag);
          element.focus = function () { focused = this; };
          created.push(element);
          return element;
        })(document.createElement);
        const descendants = element => [element, ...(element.children || []).flatMap(descendants)];
        // The inspector stub keeps earlier renders' children, so read only the latest render's,
        // which starts with its Node ID field.
        const latestInspector = () => {
          const children = inspector.children;
          let start = 0;
          children.forEach((child, index) => {
            if (child.innerHTML === '<label>Node ID</label>') start = index;
          });
          return children.slice(start);
        };
        const controls = () => latestInspector().flatMap(descendants);
        inspector.querySelector = selector => {
          const match = /^\[([a-z-]+)="([^"]*)"\]$/.exec(selector);
          return match ? controls().filter(item => item.attributes?.[match[1]] === match[2]).slice(-1)[0] || null : null;
        };
        const show = id => { select('node', id); };
        const block = id => latestInspector().find(child => child.id === 'signal-catalog-' + id);
        const details = id => block(id).children.find(child => child.className === 'signal-catalog-details');
        const addButton = id => block(id).children.find(child => child.tagName === 'BUTTON');
        const lines = id => details(id).children.map(child => child.tagName === 'UL'
          ? child.children.map(item => item.children.map(part => part.textContent).join('')).join(' | ')
          : child.textContent);
        const nameInput = () => controls().find(item => item.attributes?.['data-signal-name'] === 'true');
        const suggestions = () => {
          const input = nameInput();
          const list = controls().find(item => item.tagName === 'DATALIST' && item.id === input.attributes.list);
          return list ? list.children.map(option => ({ value: option.value, label: option.label ?? null })) : null;
        };
        const type = (value, commit = false) => {
          const input = nameInput();
          input.value = value;
          input.oninput();
          if (commit) (input.eventListeners.change || []).forEach(entry => entry.listener.call(input, { type: 'change' }));
        };
        // The editor-root click listener records the click's change, as a real click would.
        const click = button => {
          button.onclick();
          document.dispatchEvent({ type: 'click', target: button, bubbles: true,
            preventDefault() {}, stopPropagation() {}, stopImmediatePropagation() {} });
        };
        const copy = value => JSON.parse(JSON.stringify(value));
        """;

    // The editor-root listeners queue their history commit as a microtask.
    private const string BeforeMain = """
        if (typeof queueMicrotask === 'undefined') globalThis.queueMicrotask = callback => { Promise.resolve().then(callback); };
        """;

    [Fact]
    public void WithoutTheCapability_TheNameFieldAndInspectorAreUnchanged()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(catalogDiagram());
              show(9);
              const onCatch = {
                block: !!block(9), suggestions: suggestions(),
                describedBy: nameInput().attributes['aria-describedby'].split(' ').length
              };
              // Without diagram names there is no list at all, as before.
              const lonely = catalogDiagram();
              lonely.flowNodes = lonely.flowNodes.filter(node => ![7, 12, 13, 14, 15, 16, 17, 18].includes(node.id));
              lonely.sequenceFlows = lonely.sequenceFlows.filter(flow => flow.sourceRef !== 7 && flow.targetRef !== 7);
              loadFromObject(lonely);
              show(9);
              return JSON.stringify({
                onCatch, lonelyList: nameInput().attributes.list ?? null, lonelyBlock: !!block(9),
                list: signalCatalogCache.list, entries: signalCatalogCache.entries.size, calls: calls.length
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.False(root.GetProperty("onCatch").GetProperty("block").GetBoolean());
        Assert.Equal(
            ["claims.archived", "claims.closed", "claims.markup", "claims.unknown", "warehouse.product-recall"],
            root.GetProperty("onCatch").GetProperty("suggestions").EnumerateArray().Select(item => item.GetProperty("value").GetString()));
        Assert.All(root.GetProperty("onCatch").GetProperty("suggestions").EnumerateArray(),
            item => Assert.Equal(JsonValueKind.Null, item.GetProperty("label").ValueKind));
        // The status line and the help panel, nothing more.
        Assert.Equal(2, root.GetProperty("onCatch").GetProperty("describedBy").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lonelyList").ValueKind);
        Assert.False(root.GetProperty("lonelyBlock").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("list").ValueKind);
        Assert.Equal(0, root.GetProperty("entries").GetInt32());
        Assert.Equal(0, root.GetProperty("calls").GetInt32());
    }

    [Fact]
    public async Task Suggestions_FollowTheDiagramsNamesWithCatalogedNamesAndTheirDescriptions()
    {
        var root = await RunAsync("""
            loadFromObject(catalogDiagram());
            installCatalog();
            show(9);
            const beforeReply = suggestions();
            await answerAll();
            const onCatch = suggestions();
            show(7);
            await answerAll();
            const onThrow = suggestions();
            show(15);
            await answerAll();
            const onEnd = suggestions();
            return { beforeReply, onCatch, onThrow, onEnd, lists: calls.filter(call => call === 'entries').length };
            """);

        Assert.Equal(
            ["claims.archived", "claims.closed", "claims.markup", "claims.unknown", "warehouse.product-recall"],
            Values(root.GetProperty("beforeReply")));
        // The diagram's names first, labelled when cataloged, then the cataloged names the diagram lacks.
        var onCatch = root.GetProperty("onCatch").EnumerateArray().ToArray();
        Assert.Equal(
            ["claims.archived", "claims.closed", "claims.markup", "claims.unknown", "warehouse.product-recall",
             "claims.empty", "claims.internal"],
            onCatch.Select(item => item.GetProperty("value").GetString()));
        Assert.Equal(JsonValueKind.Null, onCatch[0].GetProperty("label").ValueKind);
        Assert.Equal("The claims of a policy were closed.", onCatch[1].GetProperty("label").GetString());
        Assert.Equal("Before " + Markup + " after", onCatch[2].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, onCatch[3].GetProperty("label").ValueKind);
        Assert.Equal("A supplier recalled a product. Open orders for it stop.", onCatch[4].GetProperty("label").GetString());
        Assert.Equal("Nothing to say.", onCatch[5].GetProperty("label").GetString());
        Assert.Equal("Thrown by people only.", onCatch[6].GetProperty("label").GetString());
        // A throw or end event is offered only the entries that let workflows throw.
        Assert.Equal(
            ["claims.archived", "claims.closed", "claims.markup", "claims.unknown", "warehouse.product-recall", "claims.empty"],
            Values(root.GetProperty("onThrow")));
        Assert.Equal(Values(root.GetProperty("onThrow")), Values(root.GetProperty("onEnd")));
        Assert.Equal(1, root.GetProperty("lists").GetInt32());
    }

    [Fact]
    public async Task Block_ShowsTheEntryOrOneNoteAndNothingForAnEmptyOrInvalidName()
    {
        var root = await RunAsync("""
            loadFromObject(catalogDiagram());
            installCatalog();
            show(9);
            const pendingLines = lines(9);
            await answerAll();
            const entry = {
              lines: lines(9), role: block(9).attributes.role, live: block(9).attributes['aria-live'],
              hidden: addButton(9).hidden, describedBy: nameInput().attributes['aria-describedby'].split(' ')
            };
            show(12);
            await answerAll();
            const notThrowable = { lines: lines(12), hidden: addButton(12).hidden };
            show(14);
            await answerAll();
            const archived = { lines: lines(14), hidden: addButton(14).hidden };
            show(16);
            await answerAll();
            const missing = { lines: lines(16), hidden: addButton(16).hidden };
            type('');
            const empty = lines(16);
            type('Claims.Unknown', true);
            await answerAll();
            const invalid = lines(16);
            type('claims.empty');
            await answerAll();
            const noFields = { lines: lines(16), hidden: addButton(16).hidden };
            return { pendingLines, entry, notThrowable, archived, missing, empty, invalid, noFields, calls };
            """);

        Assert.Empty(root.GetProperty("pendingLines").EnumerateArray());
        var entry = root.GetProperty("entry");
        Assert.Equal(
            ["Signal catalog entry", "A supplier recalled a product. Open orders for it stop.",
             "reason string, required | skus string[], required | lot-number string, optional | details json, optional"],
            Values(entry.GetProperty("lines")));
        Assert.Equal("status", entry.GetProperty("role").GetString());
        Assert.Equal("polite", entry.GetProperty("live").GetString());
        Assert.False(entry.GetProperty("hidden").GetBoolean());
        Assert.Equal(["signal-name-status-9", "signal-catalog-9-details"],
            Values(entry.GetProperty("describedBy")).Take(2));

        Assert.Equal(["Workflows may not throw this signal."], Values(root.GetProperty("notThrowable").GetProperty("lines")));
        Assert.True(root.GetProperty("notThrowable").GetProperty("hidden").GetBoolean());
        Assert.Equal(["Archived in the signal catalog."], Values(root.GetProperty("archived").GetProperty("lines")));
        Assert.True(root.GetProperty("archived").GetProperty("hidden").GetBoolean());
        Assert.Equal(["Not in the signal catalog."], Values(root.GetProperty("missing").GetProperty("lines")));
        Assert.True(root.GetProperty("missing").GetProperty("hidden").GetBoolean());
        Assert.Empty(root.GetProperty("empty").EnumerateArray());
        Assert.Empty(root.GetProperty("invalid").EnumerateArray());
        Assert.Equal(["Signal catalog entry", "Nothing to say.", "This signal has no payload fields."],
            Values(root.GetProperty("noFields").GetProperty("lines")));
        Assert.True(root.GetProperty("noFields").GetProperty("hidden").GetBoolean());
        // The invalid name was never asked for.
        Assert.DoesNotContain("entry:Claims.Unknown", Values(root.GetProperty("calls")));
    }

    [Fact]
    public async Task Block_SaysWhenTheDeploymentRequiresAnEntry()
    {
        var root = await RunAsync("""
            loadFromObject(catalogDiagram());
            installCatalog({ mode: 'required' });
            show(16);
            await answerAll();
            const missing = lines(16);
            show(9);
            await answerAll();
            return { missing, entry: lines(9)[0] };
            """);

        Assert.Equal(["Not in the signal catalog. This deployment requires an entry before publication."],
            Values(root.GetProperty("missing")));
        Assert.Equal("Signal catalog entry", root.GetProperty("entry").GetString());
    }

    [Fact]
    public async Task CatalogText_IsShownAsTextAndCreatesNoElement()
    {
        var root = await RunAsync("""
            loadFromObject(catalogDiagram());
            installCatalog();
            show(17);
            await answerAll();
            const parts = descendants(block(17));
            const fieldItem = details(17).children.find(child => child.tagName === 'UL').children[0];
            const option = controls().find(item => item.tagName === 'OPTION' && item.value === 'claims.markup');
            return {
              lines: lines(17),
              fieldName: fieldItem.children[0].textContent,
              fieldTitle: fieldItem.title,
              markupInHtml: parts.filter(part => String(part.innerHTML || '') !== '').length,
              images: created.filter(element => element.tagName === 'IMG').length,
              optionLabel: option ? option.label : null,
              optionHtml: option ? option.innerHTML : null
            };
            """);

        Assert.Equal(
            ["Signal catalog entry", "Before " + Markup + " after", Markup + " string, optional"],
            Values(root.GetProperty("lines")));
        Assert.Equal(Markup, root.GetProperty("fieldName").GetString());
        Assert.Equal(Markup, root.GetProperty("fieldTitle").GetString());
        Assert.Equal(0, root.GetProperty("markupInHtml").GetInt32());
        Assert.Equal(0, root.GetProperty("images").GetInt32());
        Assert.Equal("Before " + Markup + " after", root.GetProperty("optionLabel").GetString());
        Assert.Equal(string.Empty, root.GetProperty("optionHtml").GetString());
    }

    [Theory]
    // The throw sends 'Reason' already, which matches the field ignoring case. skus is declared as a number,
    // lot-number is no identifier, and the declared Details keeps its spelling.
    [InlineData(7, """[{"name":"skus","required":true},{"name":"lot-number"},{"name":"details","variable":"Details"}]""")]
    [InlineData(15, """[{"name":"reason","variable":"reason","required":true},{"name":"skus","required":true},{"name":"lot-number"},{"name":"details","variable":"Details"}]""")]
    public async Task AddMissingFields_SendsEachFieldFromAThrowOrEndEventAsOneUndoableChange(int nodeId, string expected)
    {
        var root = await RunAsync($$"""
            loadFromObject(catalogDiagram());
            installCatalog();
            show({{nodeId}});
            await answerAll();
            resetHistory();
            const before = JSON.stringify(model);
            const original = copy(getNode({{nodeId}}).signal.payload);
            click(addButton({{nodeId}}));
            await settle();
            const after = {
              kept: copy(getNode({{nodeId}}).signal.payload.slice(0, original.length)),
              payload: copy(getNode({{nodeId}}).signal.payload.slice(original.length)),
              history: undoHistory.length,
              dirty: JSON.stringify(model) !== before,
              focused: focused && focused.attributes['data-signal-payload-name'],
              hidden: addButton({{nodeId}}).hidden
            };
            undo();
            const undone = { payload: copy(getNode({{nodeId}}).signal.payload), clean: JSON.stringify(model) === before };
            return { original, after, undone, calls };
            """);

        var after = root.GetProperty("after");
        Assert.Equal(root.GetProperty("original").GetRawText(), after.GetProperty("kept").GetRawText());
        Assert.Equal(expected, after.GetProperty("payload").GetRawText());
        Assert.Equal(1, after.GetProperty("history").GetInt32());
        Assert.True(after.GetProperty("dirty").GetBoolean());
        var firstNew = root.GetProperty("original").GetArrayLength();
        Assert.Equal(firstNew.ToString(System.Globalization.CultureInfo.InvariantCulture), after.GetProperty("focused").GetString());
        Assert.True(after.GetProperty("hidden").GetBoolean());
        var undone = root.GetProperty("undone");
        Assert.Equal(root.GetProperty("original").GetRawText(), undone.GetProperty("payload").GetRawText());
        Assert.True(undone.GetProperty("clean").GetBoolean());
    }

    [Theory]
    // The catch maps reason already; skus is declared as a number and lot-number is no identifier.
    [InlineData(9, """[{"variable":"","path":"skus","dataType":"string","isArray":true,"required":true,"defaultValue":null,"validation":null},{"variable":"","path":"lot-number","dataType":"string","isArray":false,"required":false,"defaultValue":null,"validation":null},{"variable":"Details","path":"details","dataType":"json","isArray":false,"required":false,"defaultValue":null,"validation":null}]""")]
    [InlineData(18, """[{"variable":"reason","path":"reason","dataType":"string","isArray":false,"required":true,"defaultValue":null,"validation":null},{"variable":"","path":"skus","dataType":"string","isArray":true,"required":true,"defaultValue":null,"validation":null},{"variable":"","path":"lot-number","dataType":"string","isArray":false,"required":false,"defaultValue":null,"validation":null},{"variable":"Details","path":"details","dataType":"json","isArray":false,"required":false,"defaultValue":null,"validation":null}]""")]
    // A start's mappings declare start variables, which cannot reuse a process variable's name.
    [InlineData(13, """[{"variable":"","path":"reason","dataType":"string","isArray":false,"required":true,"defaultValue":null,"validation":null},{"variable":"","path":"skus","dataType":"string","isArray":true,"required":true,"defaultValue":null,"validation":null},{"variable":"","path":"lot-number","dataType":"string","isArray":false,"required":false,"defaultValue":null,"validation":null},{"variable":"","path":"details","dataType":"json","isArray":false,"required":false,"defaultValue":null,"validation":null}]""")]
    public async Task AddMissingFields_MapsEachFieldIntoACatchBoundaryOrStartAsOneUndoableChange(int nodeId, string expected)
    {
        var root = await RunAsync($$"""
            loadFromObject(catalogDiagram());
            installCatalog();
            show({{nodeId}});
            await answerAll();
            resetHistory();
            const before = JSON.stringify(model);
            const original = copy(getNode({{nodeId}}).signal.outputMappings);
            click(addButton({{nodeId}}));
            await settle();
            const after = {
              kept: copy(getNode({{nodeId}}).signal.outputMappings.slice(0, original.length)),
              mappings: copy(getNode({{nodeId}}).signal.outputMappings.slice(original.length)),
              history: undoHistory.length,
              dirty: JSON.stringify(model) !== before,
              focused: focused && focused.attributes['data-signal-mapping'],
              hidden: addButton({{nodeId}}).hidden
            };
            undo();
            const undone = { mappings: copy(getNode({{nodeId}}).signal.outputMappings), clean: JSON.stringify(model) === before };
            return { original, after, undone };
            """);

        var after = root.GetProperty("after");
        Assert.Equal(root.GetProperty("original").GetRawText(), after.GetProperty("kept").GetRawText());
        Assert.Equal(expected, after.GetProperty("mappings").GetRawText());
        Assert.Equal(1, after.GetProperty("history").GetInt32());
        Assert.True(after.GetProperty("dirty").GetBoolean());
        var firstNew = root.GetProperty("original").GetArrayLength();
        Assert.Equal(firstNew.ToString(System.Globalization.CultureInfo.InvariantCulture), after.GetProperty("focused").GetString());
        Assert.True(after.GetProperty("hidden").GetBoolean());
        var undone = root.GetProperty("undone");
        Assert.Equal(root.GetProperty("original").GetRawText(), undone.GetProperty("mappings").GetRawText());
        Assert.True(undone.GetProperty("clean").GetBoolean());
    }

    [Fact]
    public async Task AddMissingFields_FollowsThePayloadAndThePathsAsTheyAreEdited()
    {
        var root = await RunAsync("""
            loadFromObject(catalogDiagram());
            installCatalog();
            show(9);
            await answerAll();
            const pathInput = () => controls().filter(item => item.tagName === 'INPUT' && item.placeholder === 'version')[0];
            click(addButton(9));
            await settle();
            const afterAdd = addButton(9).hidden;
            // Editing the first mapping's path goes through the editor-root input listener, which redraws the block.
            inspector.contains = () => true;
            const edit = value => {
              const input = pathInput();
              input.value = value;
              input.oninput();
              document.dispatchEvent({ type: 'input', target: input, bubbles: true,
                preventDefault() {}, stopPropagation() {}, stopImmediatePropagation() {} });
              return addButton(9).hidden;
            };
            const afterRename = edit('reasons');
            const afterPart = edit('reason.code');
            return { afterAdd, afterRename, afterPart };
            """);

        Assert.True(root.GetProperty("afterAdd").GetBoolean());
        Assert.False(root.GetProperty("afterRename").GetBoolean());
        // A path below a field reads that field, so it is not missing.
        Assert.True(root.GetProperty("afterPart").GetBoolean());
    }

    [Fact]
    public async Task Replies_NeverChangeTheDocumentAndEachEntryIsAskedForOnce()
    {
        var root = await RunAsync("""
            loadFromObject(catalogDiagram());
            installCatalog();
            resetHistory();
            const before = JSON.stringify(model);

            // A reply for a node that is no longer selected redraws nothing and asks for nothing.
            show(9);
            show(2);
            await answerAll();
            const afterLeaving = calls.slice();

            // The same node, still selected, is redrawn in place when its entry arrives.
            show(9);
            const shownBlock = block(9);
            const inspectorSize = inspector.children.length;
            await answerAll();
            const inPlace = { same: block(9) === shownBlock, size: inspector.children.length === inspectorSize, lines: lines(9).length };

            const unchanged = { model: JSON.stringify(model) === before, history: undoHistory.length };

            // Other events with the same name, a redraw, and typing reuse the cached entry.
            show(13); show(7); show(9);
            await answerAll();
            show(16);
            await answerAll();
            show(16);
            await answerAll();
            type('claims.emp');
            type('claims.empty');
            await answerAll();
            type('claims.unknown');
            type('claims.unknown', true);
            await answerAll();
            return { afterLeaving, inPlace, unchanged, calls };
            """);

        Assert.Equal(["entries"], Values(root.GetProperty("afterLeaving")));
        var inPlace = root.GetProperty("inPlace");
        Assert.True(inPlace.GetProperty("same").GetBoolean());
        Assert.True(inPlace.GetProperty("size").GetBoolean());
        Assert.Equal(3, inPlace.GetProperty("lines").GetInt32());
        Assert.True(root.GetProperty("unchanged").GetProperty("model").GetBoolean());
        Assert.Equal(0, root.GetProperty("unchanged").GetProperty("history").GetInt32());
        // A name the list lacks is asked for when drawn or committed, never on a keystroke.
        Assert.Equal(
            ["entries", "entry:warehouse.product-recall", "entry:claims.unknown", "entry:claims.empty"],
            Values(root.GetProperty("calls")));
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("unavailable")]
    public async Task AFailedListLeavesTheEditorAsItIsWithoutACatalog(string status)
    {
        var root = await RunAsync($$"""
            loadFromObject(catalogDiagram());
            installCatalog({ listStatus: '{{status}}' });
            show(9);
            await answerAll();
            const onCatch = { lines: lines(9), hidden: addButton(9).hidden, suggestions: suggestions().map(item => item.value) };
            show(16);
            type('claims.other', true);
            await answerAll();
            return { onCatch, other: lines(16), calls };
            """);

        Assert.Empty(root.GetProperty("onCatch").GetProperty("lines").EnumerateArray());
        Assert.True(root.GetProperty("onCatch").GetProperty("hidden").GetBoolean());
        Assert.Equal(
            ["claims.archived", "claims.closed", "claims.markup", "claims.unknown", "warehouse.product-recall"],
            Values(root.GetProperty("onCatch").GetProperty("suggestions")));
        Assert.Empty(root.GetProperty("other").EnumerateArray());
        Assert.Equal(["entries"], Values(root.GetProperty("calls")));
    }

    private static async Task<JsonElement> RunAsync(string body)
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(BeforeMain, mode: "hosted");
        var json = (await engine.EvaluateAsync(Diagram + "(async () => {\n" + body + "\n})().then(value => JSON.stringify(value))"))
            .AsString();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string[] Values(JsonElement array) =>
        array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object
            ? item.GetProperty("value").GetString()!
            : item.GetString()!).ToArray();
}
