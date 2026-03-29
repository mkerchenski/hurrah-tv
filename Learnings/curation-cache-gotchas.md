# Curation Cache: Never Cache Empty Results

> **Area:** API | AI
> **Date:** 2026-03-29

## Context
The "Curated for You" section was invisible on the home page. Debug logging showed the API returning `{"rows":[],"fromCache":true}` — the cache had a valid hash match but contained empty data from a previous failed AI call.

## Learning
When caching AI-generated content with hash-based invalidation:

1. **Never cache empty/failed results** — if the AI call fails or returns no rows, don't write to cache. Otherwise the hash matches on next load and the empty result is served forever (until the user changes their watchlist).

2. **Skip `"[]"` in cache reads** — even with the write guard, old empty entries may exist. Add `&& cached.Value.rowsJson != "[]"` to the cache hit check.

3. **Force-refresh must work** — the `/refresh` endpoint writes `"[]"` with hash `"force-refresh"` to invalidate. This guarantees the next read misses cache (real hash won't equal `"force-refresh"`). But if the subsequent AI call fails, you're back to an empty cache entry — which is why rule #1 matters.

## Example
```csharp
// only cache non-empty results
if (rows.Count > 0)
{
    string rowsJson = JsonSerializer.Serialize(rows);
    await db.SetCurationCacheAsync(userId, rowsJson, currentHash);
}

// cache read: skip empty entries
if (cached != null && cached.Value.watchlistHash == currentHash
    && cached.Value.rowsJson != null && cached.Value.rowsJson != "[]")
{
    List<AICuratedRow> cachedRows = JsonSerializer.Deserialize<...>(cached.Value.rowsJson) ?? [];
    if (cachedRows.Count > 0)
        return new CurationResult { Rows = cachedRows, FromCache = true };
}
```
