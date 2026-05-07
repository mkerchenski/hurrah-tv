# SortableJS via JS Interop + Coexisting With Existing Touch Gestures

> **Area:** UI | WASM
> **Date:** 2026-05-07

## Context
Issue #39 added drag-to-reorder on the queue's Want to Watch tab. The page already uses `hurrahLongPress` on poster cards to open QuickActions. Both gestures live on the same row — long-press on the body opens QuickActions, drag from the handle reorders. Wired naively, a long-press *anywhere* on the row including on the drag handle fires QuickActions, and the SortableJS drag never starts on touch devices.

## Learning

Three distinct things have to be right for a JS-interop drag library to coexist with an existing touch gesture in Blazor WASM:

### 1. Restrict the drag affordance to a specific child element via `handle:`

`Sortable.create(el, { handle: '.drag-handle', ... })` makes the rest of the row inert to drag. This is what keeps the `<a href>` and other clickable children working — without `handle:`, every child element of the list participates in drag detection and ordinary clicks get swallowed.

Critical SortableJS options for a Blazor list:

```javascript
{
    handle: '.drag-handle',
    delay: 0,
    delayOnTouchOnly: true,    // immediate on mouse, settable delay on touch
    touchStartThreshold: 5,    // 5px movement before drag starts → vertical scroll still works
    animation: 150,
    ghostClass: 'sortable-ghost',
    chosenClass: 'sortable-chosen',
    dragClass: 'sortable-drag'
}
```

### 2. Teach the *other* gesture to skip when the touch starts on the handle

SortableJS's `handle:` only stops *Sortable* from reacting elsewhere. Existing gestures on the row body still see touch events on the handle. Add a one-line guard at the top of any other touch handler:

```javascript
const onStart = (e) => {
    if (e.target?.closest?.('.drag-handle')) return;
    // ... rest of long-press / swipe / etc. logic
};
```

This is cheaper than restructuring the markup to lift the handle outside the long-press element.

### 3. Use the JS-interop handle lifecycle pattern (existing learning)

`hurrahSortableInit` returns a JS object; the C# side holds an `IJSObjectReference` and disposes via `hurrahSortableDispose` + `IJSObjectReference.DisposeAsync`. Same shape as `hurrahLongPress` (see `blazor-js-interop-handle-lifecycle.md`). Don't use `InvokeVoidAsync` — the handle would be lost.

### Computing the target Position from a SortableJS event in a *filtered* list

SortableJS's `onEnd` reports `oldIndex` / `newIndex` in the rendered DOM, which for a filtered queue view is **a subset of the underlying list**. The reorder endpoint takes an absolute Position. Translating:

```csharp
// in [JSInvokable] OnReorder(itemId, oldIndex, newIndex)
List<QueueItem> visible = FilteredItems.ToList();
QueueItem dragged = visible.First(i => i.Id == itemId);
QueueItem target  = visible[newIndex];
int targetPos = target.Position;
// → set dragged.Position = target.Position; the server's ReorderAsync shifts
//   the displaced item out of the way. Net effect: dragged lands exactly where
//   target used to be in the visible list, and other items keep their order.
```

This works for any filter (status tab, media type, both) because Position is a strict total order — slotting a single item to an absolute Position never disturbs the relative order of the items not in the visible subset.

### Optimistic UI with snapshot revert

Snapshot via `[.._items]` (cheap shallow copy of the list — items inside are references, untouched), apply the visible reorder locally, fire the API call, revert from the snapshot in the failure path. Track per-item in-flight state with a `HashSet<int>` if you need a visual disabled state on the dragged row.

## Why each piece matters separately

- Without `handle:` → ordinary clicks on `<a>` children get hijacked.
- Without the long-press guard → long-pressing the handle opens QuickActions instead of starting a drag.
- Without `touchStartThreshold` → vertical scrolling on touch devices triggers a drag.
- Without `delayOnTouchOnly: true` → desktop drag feels laggy if you also set `delay`.
- Without snapshot revert → a 5xx leaves the optimistic UI lying.

## File pointers
- `HurrahTv.Client/wwwroot/index.html` — `hurrahSortableInit`, `hurrahSortableDispose`, plus the `.drag-handle` skip in `hurrahLongPress.onStart`
- `HurrahTv.Client/Pages/Queue.razor` — wiring (`OnAfterRenderAsync` attach/detach by tab, `[JSInvokable] OnReorder`, `IAsyncDisposable`)
- `HurrahTv.Api/Services/DbService.cs` — `ReorderAsync` (transactional shift, accepts absolute target Position)
