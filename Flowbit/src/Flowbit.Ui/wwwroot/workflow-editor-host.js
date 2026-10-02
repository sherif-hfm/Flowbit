// Thin Blazor adapter. The shared editor owns all markup and interactions.
const assets = new Map();

function loadAsset(url, stylesheet = false) {
    if (assets.has(url)) return assets.get(url);
    const pending = new Promise((resolve, reject) => {
        const element = document.createElement(stylesheet ? "link" : "script");
        if (stylesheet) { element.rel = "stylesheet"; element.href = url; }
        else element.src = url;
        element.onload = resolve;
        element.onerror = () => {
            element.remove();
            assets.delete(url);
            reject(new Error(`Could not load editor asset: ${url}. Refresh after restarting the UI if assets were added during development.`));
        };
        document.head.append(element);
    });
    assets.set(url, pending);
    return pending;
}

export async function create(root, dotnet, urls) {
    await Promise.all([loadAsset(urls.stylesheet, true), loadAsset(urls.template)]);
    await loadAsset(urls.script);
    if (!root.isConnected) throw new Error("Editor is closed.");
    let disposed = false;
    let snapshot = null;
    const notify = (method, ...args) => {
        if (!disposed) dotnet.invokeMethodAsync(method, ...args).catch(() => {});
    };
    const editor = window.FlowbitEditor.mount(root, {
        mode: "hosted",
        onChange: state => notify("EditorChanged", state.dirty),
        onReplace: () => { snapshot = null; notify("EditorReplaced"); },
        onSaveRequested: () => disposed ? Promise.resolve() : dotnet.invokeMethodAsync("SaveEditor")
    });
    const removed = new MutationObserver(() => { if (!root.isConnected) dispose(); });
    removed.observe(document.body, { childList: true, subtree: true });
    function dispose() {
        if (disposed) return;
        disposed = true;
        removed.disconnect();
        editor.dispose();
    }
    return {
        async load(stream, key, fresh) {
            const json = new TextDecoder().decode(await stream.arrayBuffer());
            if (disposed) return;
            editor.load(fresh ? null : JSON.parse(json), { key, fresh });
            snapshot = null;
        },
        exportStream() {
            const result = editor.prepareExport();
            if (new TextEncoder().encode(result.json).byteLength > 2 * 1024 * 1024)
                throw new Error("Workflow exceeds the 2 MiB limit for saving to Flowbit.");
            snapshot = result;
            // InvokeAsync<IJSStreamReference> wraps the Blob in the framework.
            return new Blob([result.json], { type: "application/json" });
        },
        saved(key) {
            if (!snapshot) throw new Error("The document was replaced while the save was in progress.");
            editor.acknowledgeSaved(snapshot.snapshotId, key);
            snapshot = null;
        },
        setHostState: state => editor.setHostState(state),
        confirmLeave() {
            return !editor.getState().dirty || window.confirm("Leave the editor? Unsaved changes will be lost.");
        },
        dispose
    };
}
