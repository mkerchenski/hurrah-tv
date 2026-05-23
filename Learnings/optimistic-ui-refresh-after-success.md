# Optimistic UI Must Refresh Local State After Server Success, Not Just Revert on Failure

> **Area:** UI | WASM | API
> **Date:** 2026-05-23
> **Resolves:** mkerchenski/hurrah-tv#101

## Context

`Queue.razor`'s drag-reorder uses optimistic UI. After fixing the server-side ORDER BY for #101 (Want-to-Watch now sorts by `Position` only), the first drag persisted correctly — but the **second** drag in the same session always reverted on refresh.

The PUT to `/api/queue/{id}/position` returned 200. The DB was unchanged. On refresh, the server returned the queue in its pre-second-drag order. The user's second mutation evaporated.

Walkthrough:

1. Initial GET returns three items with `Position = 0, 1, 2`. C# `_items` holds objects with those Position properties.
2. User drags B (position 1) to where A (position 0) is. C# does `_items.Remove(B); _items.Insert(0, B);` — the list order is now `[B, A, C]`, but **`A.Position`, `B.Position`, `C.Position` are unchanged**.
3. PUT `B.Id → 0` succeeds. Server runs `ReorderAsync`: B is now Position 0, A bumps to Position 1, C stays at 2.
4. **Client never refetches.** `_items` is `[B, A, C]` with `Position = 1, 0, 2` — the references moved but the Position field on each object is stale.
5. User drags A (now visually at index 1, with stale `Position = 0`) to the top. C# reads `target = _items[0] = B`, `targetPos = B.Position = 1` (stale!).
6. PUT `A.Id → 1` arrives at the server. `ReorderAsync` reads A's current server-side position: also 1. `newPosition == oldPosition` → commit a no-op, return 200.
7. UI shows `[A, B, C]` optimistically. Refresh GET returns server state `[B, A, C]` — second drag reverted.

The bug isn't in the server, the snapshot logic, or the version token. It's that `_items` was reordered but never *reconciled* with the server's new state.

## Learning

**Optimistic UI is a two-phase operation:**

- **Phase 1 — local mutation**: apply the user's intent to in-memory state and re-render. Snapshot the old state for revert-on-failure.
- **Phase 2 — reconcile**: on success, refresh the local copy from the server (or apply the server's response directly). On failure, restore the snapshot.

Skipping Phase 2 doesn't break the *first* operation — it breaks every operation after it. The local state is now a half-truth: the visible *ordering* matches the server, but each element's *data fields* still hold the pre-mutation values. The next operation reads those stale fields into its request payload and ships garbage.

The cheap fix is `_items = await Api.GetAsync()` after a successful mutate. The expensive optimization is "return the affected rows from the mutating endpoint and merge in place." Either works; doing neither breaks consecutive mutations.

This is distinct from `[[optimistic-update-version-token]]`:

| Concern | Pattern |
|---|---|
| **Failure** of an older op clobbering a newer op's local state | Version token — only revert if `version == _currentVersion` |
| **Success** of any op leaving subsequent ops reading stale fields | Refresh local state after success |

Both are needed. The version-token learning covers the *revert* side; this covers the *reconcile* side. Together they cover the full life of an optimistic mutation.

## Example

`HurrahTv.Client/Pages/Queue.razor`, `MoveItemAsync`:

```csharp
bool apiOk = await Api.UpdateQueuePositionAsync(dragged.Id, targetPos);
shouldRevert = !apiOk;

// refresh in-memory Position values from server. The local Remove+Insert above
// moved the QueueItem references into the new order, but each item's Position
// property still holds the value from the initial GET. The next drag would read
// those stale values into targetPos and ship a no-op PUT (newPosition ==
// oldPosition on the server). Pins #101 client-side.
if (apiOk && version == _reorderVersion)
{
    List<QueueItem> fresh = await Api.GetQueueAsync();
    if (version == _reorderVersion)   // re-check after the await
    {
        _items = fresh;
        UpdateCounts();
        RecomputeVisible();
    }
}
```

The double `version == _reorderVersion` check matters: it avoids the GET if a newer drag has already fired, and discards the GET's result if a newer drag fires *during* the GET.

## Symptom for future debugging

If a mutation works once and then "breaks" on the second try — and the second PUT returns 200 but the server state is unchanged — this is the pattern. The check: log the actual payload going to the server on the second mutation. If it carries pre-first-mutation values, the local copy is stale.

## Generalization

Anywhere the client mutates a resource and the server's response *or subsequent reads* would have different field values than the in-memory copy, refresh after success. Examples beyond reorder:

- Status flips that renumber positions, increment counters, or rebalance siblings (reorder, archive, promote-to-top)
- Cascade updates (`UPDATE Users SET ... ; UPDATE Audit SET ...`) where the audit row is in memory
- Anything that triggers server-side denormalization or recomputation

If the mutation is a pure overwrite of a single column on a single row (e.g. `UPDATE Users SET Name = @x WHERE Id = @y`), local state can be updated in-place from the request payload without a refresh. Reorder isn't that — it shifts other rows.
