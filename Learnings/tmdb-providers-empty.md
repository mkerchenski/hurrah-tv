# TMDb Watch Providers Often Returns Empty for Non-US Content

> **Area:** TMDb | Data
> **Date:** 2026-04-05

## Context
Investigating why shows like Doraemon and some Disney movies showed no "Streaming on" section. The API call to `tv/{id}/watch/providers` returned `{"id":57624,"results":{}}` — completely empty, not even a US region entry.

## Learning
TMDb's watch provider data (sourced from JustWatch) has significant gaps:
- **Non-US originals** (Japanese anime, Korean dramas) often have zero US provider data
- **Very new releases** may not be catalogued yet by JustWatch
- **Region-exclusive content** on niche platforms may not appear
- **Live-TV / next-day-VOD content** — late-night talk shows on Hulu (Kimmel, Fallon, Colbert) are silently invisible. JustWatch tracks Hulu's on-demand catalog (`provider_id: 15`) but doesn't surface Hulu's live-TV / next-day overlap, so `watch/providers` returns `{}` for Jimmy Kimmel Live even though users actively watch it on Hulu daily. Verified empirically against `#145` (2026-05-28): `availableonjson = "[]"` even after a fresh refresh.
- TMDb returns `"results":{}` (empty object) — not a US entry with empty arrays, but literally no region data at all

The provider fetch code must handle this gracefully at every layer:
1. **Details page**: Show "No streaming info available" instead of hiding the section entirely
2. **Search results**: Mark items with `NoStreamingInfo = true` for a "Not streaming" badge
3. **Fallback chain**: User's services → all streaming → buy/rent → nothing

This is different from "not on your services" (provider data exists but none match) vs. "no data" (TMDb has nothing).
