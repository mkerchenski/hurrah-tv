# SortableJS: `forceFallback: true` for Touch Drag in Blazor WASM

> **Area:** UI | WASM
> **Date:** 2026-05-27

## Context
Queue drag-to-reorder (Want-to-Watch tab) worked on desktop with a mouse but was broken on touch: the row lifted (got `sortable-chosen`) but **no gap opened and it couldn't be dropped**. The C# path (`OnReorder` → `MoveItemAsync` → `UpdateQueuePositionAsync`) was correct, and the gesture-coexistence wiring from `sortablejs-interop-and-gesture-conflicts.md` (`handle:`, the long-press `.drag-handle` skip) was all in place. (#139)

## Learning

By default SortableJS drives dragging through the **native HTML5 drag-and-drop API** (`dragstart`/`dragover`/`drop`). That API **does not fire on touch devices** — so on a phone the library's own chosen/lift styling applies, but the reorder events that open the gap and commit the drop never happen. Desktop mouse works because native DnD works there.

The fix is one option:

```javascript
Sortable.create(el, {
    handle: '.drag-handle',
    forceFallback: true,    // use SortableJS's own pointer-driven drag on ALL platforms
    fallbackTolerance: 5,   // ignore micro-moves so a tap doesn't start a drag
    // delayOnTouchOnly, touchStartThreshold, ghostClass, etc. as before
});
```

`forceFallback` makes SortableJS simulate the drag itself (pointer/touch events it fully controls) instead of delegating to native DnD. Mouse and touch then share **one** code path, so:

- the behavior is identical across platforms, and
- a desktop mouse test now actually exercises the same path touch uses — you can validate the touch fix without a device (though real iOS Safari / Android Chrome is still the final check).

### CSS the fallback needs

In fallback mode SortableJS clones the dragged row into a floating `.sortable-fallback` (position: fixed, follows the pointer) and leaves the original in the list as the `.sortable-ghost` placeholder. The clone gets inline positioning automatically, so the drag *functions* with no CSS — but add a rule so it *reads* right:

```css
.sortable-ghost { opacity: 0.4; }   /* dim the placeholder = "lands here" */
.sortable-fallback { cursor: grabbing; }
```

These are literal class hooks; in Tailwind v4 put them as plain CSS in `Styles/input.css` (they pass straight through `npm run build:css`).

## File pointers
- `HurrahTv.Client/wwwroot/js/sortable.js` — the `forceFallback` option
- `HurrahTv.Client/Styles/input.css` — `.sortable-ghost` / `.sortable-fallback`
- Related: `Learnings/sortablejs-interop-and-gesture-conflicts.md` (handle + long-press coexistence)
