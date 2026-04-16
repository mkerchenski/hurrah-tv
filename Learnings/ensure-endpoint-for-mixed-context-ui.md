# "Ensure" Endpoint Pattern for UI Surfaces That Can't Know Prior State

> **Area:** API | UI | WASM
> **Date:** 2026-04-16

## Context
Unified QuickActions so every long-press surface shows the same Status + Sentiment controls. The dialog opens from two kinds of cards:

- `WatchlistRow` — passes a `QueueItem` (has `.Id`, knows its status)
- `PosterCard` — passes a `SearchResult` (no queue info — could be new or already queued)

When the user picked a status on a browse-result poster that turned out to already be queued, the client called `POST /api/queue`, the server returned 409 Conflict, and the status change was silently lost. The UI showed the dialog close and "nothing happen" — page reloaded but the user's picked status was never applied.

## Learning
**When a UI surface can't know whether its target already exists in a collection, don't make it guess — give it an endpoint that answers "give me the id, create it if necessary."** The client then does targeted updates against that id using the normal PUT endpoints. Three properties matter:

1. **Idempotent by design** — calling it once or N times for the same content yields the same row.
2. **Never mutates existing rows** — only inserts-if-absent. Targeted updates stay in their dedicated endpoints (`PUT /status`, `PUT /sentiment`), preserving single-responsibility.
3. **Returns the full row either way** — the caller can read the current state (status, sentiment, etc.) and decide whether an update is even needed.

This is different from "upsert with update":
- **Upsert-with-update** (the existing `/queue/seen` pattern) overwrites status on existing rows. Right semantic for "mark seen regardless of current state."
- **Ensure / find-or-create** never touches existing rows. Right semantic for "I need the id to do targeted updates."

Both patterns share the same DbService helper (`UpsertWithStatusAsync`) — the difference is a single predicate: `shouldUpdate: existing => false` for ensure vs. `existing => existing is Want or Watching` for the `/seen` upsert.

## Client-side flow becomes

```
browse card tap:
    ensured = await EnsureQueueItemAsync(searchResult)   // returns row, never 409s
    if (ensured.Status != pickedStatus)
        await UpdateStatusAsync(ensured.Id, pickedStatus)
```

Two round-trips instead of one, but the second is skipped when no update is needed. The important property: no 409 handling in the client, no "am I new or existing" branching in the dialog. Dialog code stays uniform across both card types.

## Why this beats alternatives
- **Have the client look up the queue and filter by TmdbId:** requires shipping the full queue to every page that might show a browse card; stale on concurrent edits.
- **Have `POST /queue` upsert status on duplicate:** changes the semantics of the `+` button on `PosterCard` hover (which is insert-only with default status). Harder to reason about.
- **Fetch-by-TmdbId endpoint + separate insert:** two round-trips on the cold path (not queued), one on the hot path (queued). `ensure` is always two at most and always one on the cold path too.

## When to reach for it
- Any long-press / context menu / quick-action surface invoked from a card that doesn't carry queue state.
- Any two-step "add if new, update either way" flow.
- Any place you'd otherwise write `catch (ConflictException) { /* lookup id and retry */ }`.
