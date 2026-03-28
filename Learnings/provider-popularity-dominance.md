# Large Providers Dominate Combined Discover Results

> **Area:** TMDb | API | UI
> **Date:** 2026-03-28

## Context
The home page showed content almost exclusively from Netflix and Hulu despite the user subscribing to 5+ services. Paramount+ and Peacock content was completely absent from both "New" and "Popular" sections.

## Learning

When you query TMDb's discover endpoint with multiple providers combined (`with_watch_providers=8|15|337|531|386`), results are sorted by TMDb's global popularity score. Services with larger libraries (Netflix, Hulu) dominate the top 20 results because their content has more page views, ratings, and watchlist additions on TMDb.

Smaller services like Paramount+ and Peacock have content in the database — it just never makes it to page 1 because it ranks lower in global popularity.

**The fix: per-provider interleaving.** Instead of one combined discover call, fetch results per provider in parallel and round-robin interleave them:

```
Netflix:    [show1, show2, show3, show4, show5]
Hulu:       [show1, show2, show3, show4, show5]
Paramount+: [show1, show2, show3, show4, show5]
Peacock:    [show1, show2, show3, show4, show5]

Interleaved: Netflix#1, Hulu#1, Paramount+#1, Peacock#1,
             Netflix#2, Hulu#2, Paramount+#2, Peacock#2, ...
```

Every service gets equal representation. Shows on multiple services are deduped (first occurrence wins). The per-provider calls are cached independently, so warm-cache performance is identical.

**Trade-off:** More TMDb API calls on cold cache (N providers × 1 call each vs 1 combined call). But the cache TTL is 2 hours, so after the first load it's free.

## Architectural implication

Both `PopularOnServicesAsync` and `NewOnServicesAsync` in TmdbService use the same `InterleaveByProviderAsync` helper. Any new home page section that uses discover should go through this interleave pattern rather than combining providers in a single call.
