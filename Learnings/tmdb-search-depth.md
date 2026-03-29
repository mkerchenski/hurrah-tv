# TMDb Search: Multi-Page Fetching + Query Normalization

> **Area:** TMDb | API
> **Date:** 2026-03-29

## Context
Search results were thin — users would search for a show, get few or no results on their services. The issue was that TMDb returns 20 results per page, and after filtering to the user's streaming services, many were removed. A search for "the" might return 20 results but only 3 on the user's services.

## Learning
**Fetch multiple pages in parallel.** TMDb's `/search/multi` returns paginated results (20 per page). Fetching just page 1 then filtering to user's services often leaves <5 results. Fetching pages 1-3 in parallel (`Task.WhenAll`) gives ~60 candidates before filtering, resulting in much better coverage.

Key details:
- Check `TotalPages` from page 1 before fetching more — don't fetch pages that don't exist
- Cap at 3 pages to avoid rate limits (TMDb allows ~40 req/10s)
- Deduplicate by TmdbId after merging pages (`DistinctBy`)
- Cap enrichment at 30 results (`MaxResultsToEnrich`) — provider lookups are N+1 calls

**Normalize queries from mobile keyboards.** iOS and Android keyboards often insert smart quotes (`''`, `""`) and em-dashes (`—`, `–`) instead of ASCII equivalents. These create different cache keys and may return different TMDb results. Normalize before caching and searching.

## Example
```csharp
// fetch page 1, then conditionally fetch more
TmdbPagedResponse? firstPage = await FetchSearchPageAsync(query, 1);
List<SearchResult> results = [.. ExtractResults(firstPage)];

if (firstPage.TotalPages > 1)
{
    int extraPages = Math.Min(2, firstPage.TotalPages - 1);
    var pages = await Task.WhenAll(
        Enumerable.Range(2, extraPages).Select(p => FetchSearchPageAsync(query, p)));
    results.AddRange(pages.Where(p => p != null).SelectMany(p => ExtractResults(p!)));
    results = [.. results.DistinctBy(r => r.TmdbId)];
}
```
