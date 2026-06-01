# AddToQueueAsync Is Idempotent — Returns the Existing Item on Conflict

> **Area:** API | UI
> **Date:** 2026-04-05 · **Updated:** 2026-06-01 (#155)

## Current behavior (post-#155)

`AddToQueueAsync` is **idempotent**. Adding a title already in the queue returns the
*existing* `QueueItem` (HTTP 201 with the row), not `null`/409. The DB enforces this with a
`UNIQUE (UserId, TmdbId, MediaType)` index, and the insert is `INSERT … ON CONFLICT DO NOTHING`
with a re-read of the existing row on conflict. The method only returns `null` on a genuine
failure (the pathological case where the row vanishes between insert and re-read → endpoint
returns `Results.Problem`).

So a create-then-update chain just works — no fallback fetch needed:

```csharp
// GOOD (post-#155) — the add returns the existing row on a dup, so the chain is safe
QueueItem? item = await Api.AddToQueueAsync(newItem);
if (item is not null)
    await Api.UpdateSentimentAsync(item.Id, sentiment);
```

A `null`-fallback to a secondary "find the existing item" fetch is now **dead code for the
duplicate case** (it only guards the pathological-failure null). New callers should not add one.
Existing fallbacks in `Details.razor` are harmless but redundant.

## Historical pitfall (pre-#155) — kept for context

`AddToQueueAsync` used to be create-only: it returned `null` on HTTP 409 ("already exists").
PosterCard's thumbs-up and Details' set-sentiment both did `if (added == null) return;`, which
silently dropped the sentiment update for items already in the queue. The fix at the time was to
either route through an upsert endpoint (`/api/queue/seen`) or fall back to finding the existing
item on `null`. #155 removed the root cause by making the add idempotent at the DB level.

## Broader pattern

Prefer making a create idempotent at the DB layer (`UNIQUE` + `ON CONFLICT`) over teaching every
caller to handle a duplicate-signalling `null`/409. The constraint is the single source of truth;
callers stay simple. See [[partial-dto-put-full-row-upsert]] for the related upsert-return shape.
