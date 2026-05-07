# JS Interop Init Can Fail Transiently — Gate "Attached" State on the Handle

> **Area:** WASM | UI
> **Date:** 2026-05-07

## Context

`Queue.razor`'s `OnAfterRenderAsync` attaches a SortableJS instance to the list element when the user is on the WantToWatch tab. The first version had this shape:

```csharp
if (wantSortable && _attachedTab != _activeTab)
{
    await DisposeSortableAsync();
    _dotNetRef ??= DotNetObjectReference.Create(this);
    _sortableHandle = await JS.InvokeAsync<IJSObjectReference>(
        "hurrahSortableInit", _listEl, _dotNetRef, nameof(OnReorder), null);
    _attachedTab = _activeTab; // ← unconditional
}
```

Works in the happy path. But `hurrahSortableInit` returns `null` if the SortableJS CDN failed to load (`if (!el || !window.Sortable) return null;`). It can also throw if the JS module errored. Either way, after the failed attempt, `_attachedTab == _activeTab` — so the next render's gate evaluates false, and **the page can't retry init until the user leaves and comes back to the tab**. The drag handle is visible but inert; the user thinks the feature is broken.

Surfaced by a Copilot review on PR #65.

## Learning

When you wire JS interop in `OnAfterRenderAsync` (or any "attach once, render many" lifecycle hook), the registration call has three possible outcomes:

1. Returns a non-null handle — success.
2. Returns null — JS-side guard tripped (dependency missing, element gone).
3. Throws — JS-side runtime error during init.

Outcomes 2 and 3 are real and recoverable. The fix:

- Wrap the `InvokeAsync<IJSObjectReference>` in `try/catch` (catch sets `_handle = null`).
- Set the "attached" / "current state" flag (`_attachedTab` here) **only when the handle is non-null**.
- Leave the gate-flag in its old/null state on failure so the next render attempts re-init.

```csharp
if (wantSortable && _attachedTab != _activeTab)
{
    await DisposeSortableAsync();
    _dotNetRef ??= DotNetObjectReference.Create(this);
    try
    {
        _sortableHandle = await JS.InvokeAsync<IJSObjectReference>(
            "hurrahSortableInit", _listEl, _dotNetRef, nameof(OnReorder), null);
    }
    catch
    {
        _sortableHandle = null;
    }
    if (_sortableHandle is not null) _attachedTab = _activeTab;
}
```

This way: a transient failure (CDN slow, JS error during a hot-reload, network blip) is recovered automatically by the next render — which fires for any unrelated state change (status toggle, tab switch, search update, etc.). The user gets a working drag handle once the dependency loads, with no manual recovery step.

## Distinct from the disposal-lifecycle learning

`blazor-js-interop-handle-lifecycle.md` covers the *cleanup* side — `IJSObjectReference` disposal, `DotNetObjectReference` disposal, dictionary-tracked per-element handles, `InvokeAsync<IJSObjectReference>` (not Void). That's about closing the loop.

This learning is about the *open* side — gating success state so failures don't permanently lock the component out of retry. Both are needed; either alone leaves a hole.

## Generalization

Whenever a registration call yields a value you'll later use to detect "am I attached?", **derive that detection from the value's existence, not from a parallel boolean you set at the call site**. The boolean drifts from reality whenever the call's outcome is non-binary; the value is authoritative.

When you can't avoid a parallel boolean (because you also care about *which* attached state you're in — e.g., "attached for the WantToWatch tab specifically"), set it conditionally on the value.

## File pointer

`HurrahTv.Client/Pages/Queue.razor` — `OnAfterRenderAsync` shows the corrected shape.
