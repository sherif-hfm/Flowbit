// Keyboard ownership stays inside the assistant; the editor keeps its own shortcuts.
export function attach(root, callback) {
    let disposed = false;
    const opener = document.getElementById("open-ai-assistant");
    const visibleControls = () => [...root.querySelectorAll("button, input, select, textarea, summary, a[href], [tabindex]")]
        .filter(element => !element.disabled && element.tabIndex >= 0 && element.getClientRects().length > 0);
    function keydown(event) {
        if (disposed || event.isComposing) return;
        if (event.key === "Escape") {
            event.preventDefault();
            event.stopPropagation();
            callback.invokeMethodAsync("CloseFromKeyboard").catch(() => {});
        } else if (event.key === "Tab" && matchMedia("(max-width: 767px)").matches) {
            const controls = visibleControls();
            if (!controls.length) { event.preventDefault(); root.focus(); return; }
            const first = controls[0], last = controls[controls.length - 1];
            if (event.shiftKey && (document.activeElement === first || document.activeElement === root)) {
                event.preventDefault(); last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault(); first.focus();
            }
        }
    }
    root.addEventListener("keydown", keydown);
    visibleControls()[0]?.focus();
    return {
        dispose() {
            if (disposed) return;
            disposed = true;
            root.removeEventListener("keydown", keydown);
            if (opener?.isConnected && (root.contains(document.activeElement) || document.activeElement === document.body))
                opener.focus();
        }
    };
}
