let activeDialog = null;

const focusableElements = (dialog) => [...dialog.querySelectorAll(
    'button:not([disabled]), input:not([disabled]), textarea:not([disabled]), select:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])'
)].filter(element => element.getClientRects().length > 0 && !element.closest('[inert]'));

export function activateDialog(element, openerId) {
    if (!element || activeDialog?.element === element) return;
    deactivateDialog();
    const previousFocus = document.activeElement;
    const onKeyDown = event => {
        if (event.key !== 'Tab') return;
        const focusable = focusableElements(element);
        const first = focusable[0];
        const last = focusable.at(-1);
        if (!first) {
            event.preventDefault();
            element.focus();
        } else if (event.shiftKey && (document.activeElement === first || document.activeElement === element)) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === element)) {
            event.preventDefault();
            first.focus();
        }
    };
    const onFocusIn = event => {
        if (element.isConnected && !element.contains(event.target)) {
            (focusableElements(element)[0] ?? element).focus();
        }
    };
    element.addEventListener('keydown', onKeyDown);
    document.addEventListener('focusin', onFocusIn);
    activeDialog = { element, openerId, previousFocus, onKeyDown, onFocusIn };
    (focusableElements(element)[0] ?? element).focus();
}

export function deactivateDialog() {
    if (!activeDialog) return;
    const { element, openerId, previousFocus, onKeyDown, onFocusIn } = activeDialog;
    activeDialog = null;
    element.removeEventListener('keydown', onKeyDown);
    document.removeEventListener('focusin', onFocusIn);
    const restore = document.getElementById(openerId)
        ?? (previousFocus?.isConnected ? previousFocus : null)
        ?? document.getElementById('task-management-refresh');
    restore?.focus();
}
