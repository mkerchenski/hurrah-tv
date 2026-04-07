# Singleton Event Bus for Cross-Component Modals (Escaping Stacking Contexts)

> **Area:** WASM | UI
> **Date:** 2026-04-07

## Context
Built a bottom-sheet quick actions modal triggered by long-press on poster cards. Initially rendered `<QuickActions>` inside each card component (PosterCard, WatchlistRow). The modal's `fixed` backdrop (`z-[60]`) was supposed to cover everything, but card badges with explicit `z-index` bled through — sparkle icons and star ratings appeared on top of the dark backdrop.

## Learning
`overflow-hidden` on a parent element creates a new **stacking context** in CSS. Elements inside that context with `z-index` paint independently from elements outside it. A `fixed` element rendered as a child of an `overflow-hidden` container can escape the clipping (position-wise), but its z-ordering is still relative to siblings within the same stacking context.

**The fix**: Render the modal at the **layout level** (MainLayout), completely outside any card/row hierarchy. Communicate between deeply nested card components and the layout-level modal via a **singleton event bus service**.

The pattern:
1. **Service** (`QuickActionService`): Simple class with `event Action<T>` for showing and `event Action` for change notifications. Registered as singleton.
2. **Modal** (`QuickActions`): Rendered in `MainLayout.razor`, subscribes to service events in `OnInitialized`, unsubscribes in `Dispose`.
3. **Triggers** (cards): Inject the service, call `ShowForQueueItem(item)` or `ShowForSearchResult(result)` from JS interop callbacks.
4. **State refresh** (pages): Subscribe to `OnChanged` event, reload data when quick actions modify items.

This also eliminates the "N modals for N cards" problem — one modal instance serves all cards.

## Example
```csharp
// service (singleton)
public class QuickActionService {
    public event Action<QueueItem>? OnShowForQueueItem;
    public event Action? OnChanged;
    public void ShowForQueueItem(QueueItem item) => OnShowForQueueItem?.Invoke(item);
    public void NotifyChanged() => OnChanged?.Invoke();
}

// in card component (deeply nested)
[Inject] QuickActionService Svc { get; set; }
[JSInvokable] public void OnLongPress(int id) {
    QueueItem? item = Items.FirstOrDefault(i => i.Id == id);
    if (item != null) Svc.ShowForQueueItem(item);
}

// in MainLayout (top-level, outside all stacking contexts)
<QuickActions />
```
