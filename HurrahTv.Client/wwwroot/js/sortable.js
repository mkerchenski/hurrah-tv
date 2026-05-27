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
        // mobile drag-reorder needs BOTH of these (#139):
        //  - forceFallback: skip the native HTML5 drag API (touch never fires it) and use
        //    SortableJS's own drag, so the row picks up on touch.
        //  - supportPointer:false: track the drag with touch/mouse events instead of Pointer
        //    Events. SortableJS defaults to Pointer Events, whose path on mobile often fails to
        //    fire the drag-over that opens the gap — the row lifts but never slots in and the
        //    drop reverts. The touch-event path opens the gap. (forceFallback alone doesn't fix
        //    this; the symptom is identical with it on or off.)
        forceFallback: true,
        supportPointer: false,
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
