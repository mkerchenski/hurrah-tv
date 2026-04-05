# AddToQueueAsync Returns Null on Conflict — Silent Failure Pitfall

> **Area:** API | UI
> **Date:** 2026-04-05

## Context
PosterCard's "thumbs up" button and Details page's "set sentiment" both tried to add an item to the queue first, then update its sentiment. `AddToQueueAsync` returns `null` on HTTP 409 (item already exists). Both callers guarded with `if (added == null) return;` — which silently dropped the sentiment update for items already in the queue.

## Learning
`AddToQueueAsync` is a create-only operation. It returns `null` on conflict, meaning "this item already exists." Code that chains operations after it (set sentiment, update status) must handle the null case by finding the existing item instead of bailing out.

Two patterns that work:
1. **Use an upsert endpoint** (`MarkAsSeenAsync` calls `/api/queue/seen` which does INSERT...ON CONFLICT UPDATE) — this always returns the item
2. **Fall back to finding the existing item** on null: fetch the queue and find by TmdbId+MediaType

Pattern that silently fails:
```csharp
// BAD — sentiment never set for existing items
QueueItem? added = await Api.AddToQueueAsync(item);
if (added != null)
    await Api.UpdateSentimentAsync(added.Id, sentiment);

// GOOD — falls back to finding existing item
QueueItem? item = await Api.AddToQueueAsync(newItem);
if (item == null)
    item = await FindExistingItemAsync(tmdbId, mediaType);
if (item != null)
    await Api.UpdateSentimentAsync(item.Id, sentiment);
```

## Broader pattern
Any time you chain a create + update, handle the "already exists" case. This applies to any endpoint that returns null/409 on duplicate.
