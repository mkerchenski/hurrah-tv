# TMDb API — How Content Discovery Works

> **Area:** TMDb | API
> **Date:** 2026-03-28

## Context
Building the Hurrah.tv home page required finding content that's relevant to the user's streaming services, genres, and freshness. TMDb has two fundamentally different paths for finding content, and choosing the wrong one (or misconfiguring parameters) can return zero results or irrelevant ones.

## Learning

### Two discovery paths

**`/trending/{type}/{window}`** — What the internet is watching right now
- Reflects real TMDb user activity (searches, ratings, page views)
- `type`: `all`, `movie`, or `tv`
- `window`: `day` or `week`
- Changes dynamically — genuinely reflects cultural moments
- **Accepts NO filters** — no provider, no genre, no date. You get what you get.
- To personalize, you must post-filter server-side (which throws away results)

**`/discover/{type}`** — The query builder (our workhorse)
- Combine any filters: providers, genres, dates, ratings, sort order
- Pre-filtered results from TMDb — efficient, full 20 per page
- But does NOT include watch provider metadata per result (no logos/names)
- To show provider badges, need a separate `/watch/providers` call per title

### Critical parameter syntax

TMDb uses two different separators with opposite meanings in the SAME parameter:

| Separator | Meaning | Example | Result |
|-----------|---------|---------|--------|
| `\|` (pipe) | **OR** | `with_genres=28\|35` | Action OR Comedy |
| `,` (comma) | **AND** | `with_genres=28,35` | Action AND Comedy |

This applies to both `with_genres` and `with_watch_providers`. Using comma for genres when you mean OR returns zero results because very few titles match ALL selected genres simultaneously.

For Hurrah.tv, we always want OR (pipe) — "show me anything matching my interests."

### Key discover parameters we use

```
discover/{mediaType}?
  with_watch_providers=8|15|337      # Netflix OR Hulu OR Disney+
  with_watch_monetization_types=flatrate  # streaming only, not buy/rent
  watch_region=US                    # US availability
  with_genres=28|35|18               # Action OR Comedy OR Drama
  first_air_date.gte=2026-01-28     # TV: released after this date
  primary_release_date.gte=2026-01-28  # Movies: released after this date
  sort_by=popularity.desc           # most popular first
  page=1                            # 20 results per page
```

Note: date parameter name differs by media type — `first_air_date` for TV, `primary_release_date` for movies.

### The N+1 badge problem

Discover tells us a show is available on one of the user's providers, but not WHICH ones. To show provider logo badges on poster cards, we call `/{type}/{id}/watch/providers` per title. With 20 results per section and 4 sections, that's up to 80 outbound TMDb calls on a cold cache.

Mitigation: 12-hour `IMemoryCache` on provider data. After first load, subsequent requests are nearly free. But cold cache (app restart, new content) is expensive.

### TMDb popularity score

`sort_by=popularity.desc` uses TMDb's rolling popularity metric — a composite of page views, votes, additions to watchlists, and release recency. It heavily favors established hits. To surface genuinely new content, combine with date filtering (`first_air_date.gte` set to ~60 days ago).

## Example

```csharp
// new TV shows on user's services in the last 60 days, matching their genre preferences
string url = $"discover/tv?api_key={key}" +
             $"&with_watch_providers={string.Join("|", providerIds)}" +
             $"&with_watch_monetization_types=flatrate" +
             $"&watch_region=US&sort_by=popularity.desc" +
             $"&first_air_date.gte={DateTime.UtcNow.AddDays(-60):yyyy-MM-dd}" +
             $"&with_genres={string.Join("|", genreIds)}";  // pipe = OR!
```
