# Adding Fields to SearchResult Requires Auditing All Creation Paths

> **Area:** Data
> **Date:** 2026-03-30

## Context
Added `OriginalLanguage` to `SearchResult` for the English-only filter. The field was populated in `MapToSearchResult` (used by discover/trending/search), but `GetDetailsAsync` builds `ShowDetails` (which inherits `SearchResult`) directly from JSON — and didn't populate the new field.

## Learning
`SearchResult` is created in multiple ways:
1. **`MapToSearchResult`** — used by discover, trending, search, recommendations. New fields added to `TmdbMultiResult` and mapped here will be populated.
2. **`GetDetailsAsync`** — manually constructs `ShowDetails` from raw JSON. New fields on the base `SearchResult` class are silently left at their default value (`""` for strings, `0` for ints).

When a filter depends on a field (like `r.OriginalLanguage == "en"`), any creation path that leaves the field at its default will cause false negatives — content gets incorrectly filtered out.

**Defense:** When filtering on a newly added field, always guard against the default/empty value:
```csharp
// BAD: hides content from GetDetailsAsync path
results.Where(r => r.OriginalLanguage == "en")

// GOOD: passes through content with unknown language
results.Where(r => r.OriginalLanguage is "" or "en")
```

Alternatively, audit every `new SearchResult` and `new ShowDetails` in the codebase whenever adding a filterable field to the base class.
