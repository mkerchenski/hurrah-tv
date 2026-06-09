# SortableJS `newIndex`/`oldIndex` Count ALL Children — Use the `*DraggableIndex` Variants

> **Area:** UI | WASM

> **Date:** 2026-06-09

## Context
Queue Want-to-Watch drag-reorder dropped every item **one slot too low** when moving down: dragging slot 1 → slot 2 landed it in slot 3. The C# `OnReorder(itemId, oldIndex, newIndex)` and its `_visibleItems[newIndex]` targeting were correct (proved by invoking the handler synthetically with `newIndex=1` → lands in slot 2). The bug was the *index value* SortableJS reported.

The list container's first child is a non-draggable aria-live `<div role="status">` (the reorder announcement region). So container children are `[statusDiv, row0, row1, ...]` — DOM child indices are shifted +1 versus the visible row order. A drag to visible slot 2 reported `evt.newIndex = 2` (counting the status div), and `OnReorder` targeted `_visibleItems[2]` = slot 3.

## Learning
SortableJS exposes **two** index pairs on its events:
- `evt.oldIndex` / `evt.newIndex` — position among **all** children of the container, including non-draggable ones.
- `evt.oldDraggableIndex` / `evt.newDraggableIndex` — position among only the elements matching the `draggable` selector.

Setting `draggable: '[data-item-id]'` gates **what can be dragged**, but does **not** change what `evt.newIndex` counts — that still includes the status div. If the C# side indexes a filtered/row-only collection (`_visibleItems`), you must pass the **draggable-filtered** indices. Verify by `Sortable.utils.index(rowEl)` (no selector → counts the status div) vs `Sortable.utils.index(rowEl, inst.options.draggable)` (row-relative).

Rule: when a sortable container has *any* non-draggable child (aria-live region, header, spacer), use `evt.oldDraggableIndex`/`evt.newDraggableIndex`. They're also the right choice generally — resilient to any future non-draggable sibling, unlike the narrower bandaid of moving the status div outside the container.

## Example
```javascript
opts.onEnd = (evt) => {
    // NOT evt.oldIndex/newIndex — they count the non-draggable aria-live <div> at child 0.
    const oldIndex = evt.oldDraggableIndex;
    const newIndex = evt.newDraggableIndex;
    if (oldIndex === newIndex) return;
    const itemId = parseInt(evt.item?.dataset?.itemId, 10);
    if (isNaN(itemId)) return;
    dotNetRef.invokeMethodAsync(callbackName, itemId, oldIndex, newIndex);
};
```
Related: [[sortablejs-interop-and-gesture-conflicts]], [[sortablejs-forcefallback-for-touch]].
