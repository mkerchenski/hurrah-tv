// longpress.js — touch long-press gesture detection for PosterCard / WatchlistRow.
// Suppresses iOS Safari's native image callout (big-poster peek) so our quick-action
// sheet wins. Extracted from inline window.hurrahLongPress[Cleanup] in index.html as
// part of issue #87.

export function attach(el, dotNetRef, callbackName, itemId) {
    if (!el) return null;
    let timer = null, startX = 0, startY = 0, fired = false, activeEvent = null;
    const onStart = (e) => {
        if (!e.touches.length) return;
        // skip when the touch starts on a sortable drag handle — Sortable owns that gesture
        if (e.target?.closest?.('.drag-handle')) return;
        const t = e.touches[0];
        startX = t.clientX; startY = t.clientY; fired = false;
        activeEvent = e;
        timer = setTimeout(() => {
            fired = true;
            // preventDefault on the live touch suppresses iOS Safari's native image callout
            // (big-poster peek / save-image sheet) that otherwise hijacks long-press
            try { activeEvent?.preventDefault?.(); } catch { }
            try { navigator.vibrate?.(50); } catch { }
            dotNetRef.invokeMethodAsync(callbackName, itemId);
        }, 500);
    };
    const onMove = (e) => {
        if (!timer || !e.touches.length) return;
        const t = e.touches[0];
        if (Math.abs(t.clientX - startX) > 10 || Math.abs(t.clientY - startY) > 10) {
            clearTimeout(timer); timer = null;
        }
    };
    const onEnd = () => { clearTimeout(timer); timer = null; activeEvent = null; };
    const onContextMenu = (e) => { if (fired) e.preventDefault(); };
    const onClick = (e) => { if (fired) { e.stopPropagation(); e.preventDefault(); fired = false; } };
    // touchstart must be non-passive so preventDefault can cancel the native callout
    el.addEventListener('touchstart', onStart, { passive: false });
    el.addEventListener('touchmove', onMove, { passive: true });
    el.addEventListener('touchend', onEnd);
    el.addEventListener('touchcancel', onEnd);
    el.addEventListener('contextmenu', onContextMenu);
    el.addEventListener('click', onClick, true);
    return { el, onStart, onMove, onEnd, onClick, onContextMenu };
}

export function cleanup(handle) {
    if (!handle?.el) return;
    handle.el.removeEventListener('touchstart', handle.onStart);
    handle.el.removeEventListener('touchmove', handle.onMove);
    handle.el.removeEventListener('touchend', handle.onEnd);
    handle.el.removeEventListener('touchcancel', handle.onEnd);
    if (handle.onContextMenu) handle.el.removeEventListener('contextmenu', handle.onContextMenu);
    handle.el.removeEventListener('click', handle.onClick, true);
}
