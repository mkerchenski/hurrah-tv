# Optimistic Updates with Snapshot Revert Need a Version Token Under Concurrency

> **Area:** UI | WASM
> **Date:** 2026-05-07

## Context

Drag-reorder on the queue uses optimistic UI: snapshot `_items`, mutate locally, fire the API call, revert on failure. The naive shape works fine in isolation but breaks when the user fires multiple drags before the first API call completes. Surfaced by a Copilot review on PR #65.

## Learning

If your optimistic-update pattern has the shape

```csharp
List<T> snapshot = [.._items];
_items = ApplyChange(_items);
StateHasChanged();
try { ok = await Api.Mutate(...); if (!ok) _items = snapshot; }
catch     { _items = snapshot; }
```

then **two overlapping operations corrupt state on failure of the older one.** Walkthrough:

1. User performs Move-1: `snapshot1 = [A, B, C]`. Local becomes `[B, C, A]`. API1 fires.
2. Before API1 returns, user performs Move-2: `snapshot2 = [B, C, A]` (the post-Move-1 state). Local becomes `[C, B, A]`. API2 fires.
3. API1 returns failure. Revert path runs: `_items = snapshot1 = [A, B, C]`. **Move-2's local mutation is silently discarded.** If API2 succeeds, server state and client state diverge until the next refetch.

The user has moved on; their newer intent should win.

### Fix: a per-call monotonic version token

```csharp
private int _operationVersion;

public async Task OnReorder(...)
{
    int version = ++_operationVersion;
    List<T> snapshot = [.._items];
    _items = ApplyChange(_items);
    StateHasChanged();

    bool shouldRevert = false;
    try
    {
        bool ok = await Api.Mutate(...);
        shouldRevert = !ok;
    }
    catch
    {
        shouldRevert = true;
    }
    finally
    {
        // a later operation has bumped _operationVersion — newer intent wins, do not clobber
        if (shouldRevert && version == _operationVersion) _items = snapshot;
        StateHasChanged();
    }
}
```

The semantics: "only revert if I'm still the most recent operation." If a newer operation is in flight or already applied, the failure of the older one is silently absorbed — its local mutation is preserved, and any divergence with the server resolves on the next refetch.

### When this rule applies

- Any `await`-mediated operation that mutates client state optimistically and reverts on failure.
- The user can plausibly fire two before the first completes (drag-reorder, rapid status toggles, batched checkbox flips, like-button spam).
- The mutation isn't naturally serialized (you didn't disable the input during in-flight, or you can't because UX demands responsiveness).

### When this rule does **not** apply

- Per-item in-flight tracking that disables the affordance per row (e.g. `_reorderingIds.Contains(item.Id)` greys out the handle on the dragged item only — but other items remain interactive). Per-item disable is a partial mitigation, not a substitute for the version token, because the user can still operate on a *different* item while one is in flight.
- Operations that can't be undone client-side anyway (e.g. delete + show toast). No revert means no clobber.
- Operations that always refetch on completion (the source of truth re-arrives, no manual revert needed).

## Anti-pattern: serializing operations to dodge the problem

"Just disable Sortable while a reorder is in flight" *works* but degrades UX. The user has to wait for the API on every drag. The version-token approach lets them keep dragging at the speed of optimistic UI; only the failure path is restricted, and failure is rare.

## Generalization

The deeper rule: **when you snapshot state to enable a revert, the snapshot is only valid for as long as no other operation has overlapped it.** Track that with a monotonic counter or a "this snapshot's generation matches current" check. Without one, snapshot revert is a time-travel bug waiting to happen.

## File pointer

`HurrahTv.Client/Pages/Queue.razor` — `OnReorder` uses `_reorderVersion`.
