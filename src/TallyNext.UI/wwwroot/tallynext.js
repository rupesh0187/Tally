// TallyNext keyboard shortcuts and focus helpers (ES module, imported via JS interop).
const shortcuts = new Set([
  'F1', 'F4', 'F5', 'F6', 'F7', 'F8', 'F9', 'Alt+F5', 'Alt+F6',
  'Alt+C', 'Alt+D', 'Alt+L', 'Alt+T', 'Alt+P', 'Alt+B', 'Alt+S', 'Alt+V',
  'Ctrl+S', 'Ctrl+Enter', 'Escape'
]);
let listener = null;

function describe(e) {
  let key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
  if (e.shiftKey && e.key.length > 1) key = 'Shift+' + key;
  if (e.ctrlKey || e.metaKey) key = 'Ctrl+' + key;
  if (e.altKey) key = 'Alt+' + key;
  return key;
}

export function register(dotnetRef) {
  unregister();
  listener = (e) => {
    const key = describe(e);
    if (!shortcuts.has(key)) return;
    // Let an open dropdown consume Escape first.
    if (key === 'Escape' && e.target instanceof Element && e.target.closest('[aria-expanded="true"]')) return;
    e.preventDefault();
    dotnetRef.invokeMethodAsync('OnShortcut', key);
  };
  document.addEventListener('keydown', listener);
}

export function unregister() {
  if (listener) document.removeEventListener('keydown', listener);
  listener = null;
}

export function focus(id) {
  const el = document.getElementById(id);
  if (!el) return false;
  el.focus();
  if (typeof el.select === 'function') el.select();
  return true;
}

// Enter-to-advance without a server round trip: an element with data-next="id" moves focus to that element
// immediately, so keystrokes typed right after Enter always land in the next field.
export function enableEnterNavigation(container) {
  if (!container || container.dataset.enterNav === '1') return;
  container.dataset.enterNav = '1';
  // Registered on window (bubble phase) so it runs after Blazor's document-level handler has already queued the
  // "Enter" event for the server; moving focus synchronously here then queues the "blur" after it, and no
  // keystroke typed immediately after Enter can land in the old field.
  const handler = (e) => {
    if (!container.isConnected) {
      window.removeEventListener('keydown', handler);
      return;
    }
    if (e.key !== 'Enter' || e.shiftKey || e.ctrlKey || e.altKey || e.metaKey) return;
    const target = e.target;
    if (!(target instanceof HTMLElement) || !container.contains(target)) return;
    const next = target.dataset.next;
    if (!next) return;
    const el = document.getElementById(next);
    if (!el) return;
    e.preventDefault();
    el.focus();
    if (typeof el.select === 'function') el.select();
  };
  window.addEventListener('keydown', handler);
}

export function back() {
  if (window.history.length > 1) window.history.back();
}

export function print() {
  window.print();
}
