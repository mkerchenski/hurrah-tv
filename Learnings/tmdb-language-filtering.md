# TMDb Language Filtering Is Endpoint-Specific

> **Area:** TMDb
> **Date:** 2026-03-30

## Context
Added an "English originals only" user preference that hides dubbed/subtitled content from the feed. Needed to filter by original language across discover, trending, and recommendation endpoints.

## Learning
TMDb's `with_original_language=en` parameter only works on the `/discover/{mediaType}` endpoint. The `/trending` and `/search/multi` endpoints do not accept this parameter — they return all languages regardless.

However, every TMDb result includes an `original_language` field in the response JSON (e.g., `"original_language": "ko"` for Korean shows). For endpoints that don't support server-side filtering, you must:
1. Include `original_language` in your deserialized model (`TmdbMultiResult`)
2. Carry it through to `SearchResult.OriginalLanguage`
3. Post-filter client-side: `results.Where(r => r.OriginalLanguage == "en")`

This means the filter operates at two tiers:
- **Discover:** server-side via URL param (most efficient, reduces API payload)
- **Trending/Search/Recommendations:** post-fetch filter on the response field

## Example
```csharp
// Discover — server-side filter
if (englishOnly)
    url += "&with_original_language=en";

// Trending/Recommendations — post-fetch filter
if (prefs.EnglishOnly)
    results = results.Where(r => r.OriginalLanguage is "" or "en").ToList();
```

The `is "" or "en"` guard is important — see `searchresult-field-gotchas.md` for why empty string must pass through.
