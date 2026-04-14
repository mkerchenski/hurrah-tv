# @key Is Required for JS Interop Stability in Dynamic Lists

> **Area:** WASM
> **Date:** 2026-04-14

## Context
WatchlistRow renders a horizontal scrolling list of poster cards. Each card registers a JS long-press handler keyed by `item.Id`. When "Episode Watched" was tapped, the card was removed from the list and `StateHasChanged()` fired. On the next long-press, QuickActions showed the wrong show's details.

## Learning
Without `@key` on list elements, Blazor's diffing algorithm reuses DOM nodes **by position**, not by identity. When an item is removed from the middle of a list, the remaining items shift positions. The JS long-press handlers registered on those DOM elements still reference the *old* item IDs — the handlers aren't re-registered until `OnAfterRenderAsync` runs, creating a window where a long-press fires with a stale item ID.

**`@key="item.Id"` fixes this** by telling Blazor to track each element by identity. When an item is removed, Blazor destroys that specific DOM element and moves others correctly — existing JS handlers on surviving elements remain bound to the right nodes.

This is especially critical when:
- JS interop handlers are bound to specific DOM elements (long-press, drag handles, resize observers)
- Items can be removed or reordered at runtime
- The handler uses a captured ID to look up state

Without `@key`, everything *looks* correct visually but JS callbacks fire for the wrong component state.

## Example
```razor
<!-- BAD — Blazor reuses DOM nodes by position; JS handlers become stale after list mutation -->
@foreach (QueueItem item in Items)
{
    <div @ref="_cardRefs[item.Id]" @onclick="() => GoToDetails(item)">
        ...
    </div>
}

<!-- GOOD — Blazor tracks each element by identity -->
@foreach (QueueItem item in Items)
{
    <div @key="item.Id" @ref="_cardRefs[item.Id]" @onclick="() => GoToDetails(item)">
        ...
    </div>
}
```
