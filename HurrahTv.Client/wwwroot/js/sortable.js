// sortable.js — drag-and-drop reordering via the global Sortable library loaded by
// the CDN <script> tag in index.html. This module just wires the C# callback into
// SortableJS's onEnd. Extracted from inline window.hurrahSortable* in index.html as
// part of issue #87.

export function init(el, dotNetRef, callbackName, options) {
    if (!el || !window.Sortable) return null;
    const opts = Object.assign({
        handle: '.drag-handle',
        animation: 150,
        delay: 0,
        delayOnTouchOnly: true,
        touchStartThreshold: 5,
        // force the JS fallback drag on every platform. without it SortableJS uses the
        // native HTML5 drag-and-drop API, which never fires on touch — the row would lift
        // (chosenClass) but no gap opens and it can't be dropped on mobile. forceFallback makes
        // mouse and touch share one pointer-driven path, so behavior is identical across both.
        // pins #139.
        forceFallback: true,
        fallbackTolerance: 5,
        ghostClass: 'sortable-ghost',
        chosenClass: 'sortable-chosen',
        dragClass: 'sortable-drag',
    }, options || {});
    opts.onEnd = (evt) => {
        if (evt.oldIndex === evt.newIndex) return;
        const itemId = parseInt(evt.item?.dataset?.itemId, 10);
        if (isNaN(itemId)) return;
        dotNetRef.invokeMethodAsync(callbackName, itemId, evt.oldIndex, evt.newIndex);
    };
    const instance = window.Sortable.create(el, opts);
    return { instance };
}

export function dispose(handle) {
    if (!handle?.instance) return;
    try { handle.instance.destroy(); } catch { }
}
