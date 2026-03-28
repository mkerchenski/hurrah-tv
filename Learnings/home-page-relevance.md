# Home Page Philosophy — Relevance Over Completeness

> **Area:** UI | API
> **Date:** 2026-03-28

## Context
The home page went through several iterations in one session: single combined list → split by media type → tabbed TV/Movies with genre filtering and dismissals. Each change was driven by the same principle.

## Learning

The home page is NOT a catalog browser. It's a personalized recommendation surface. The goal is: every poster shown is something the user might actually want to watch tonight.

This means aggressively filtering OUT content before it reaches the screen:

| Filter | Why |
|--------|-----|
| User's streaming services only | Don't show content they can't watch without signing up for something new |
| Flatrate only (no buy/rent) | They're already paying for these services — don't upsell |
| Genre preferences | Don't show horror to someone who only watches comedy |
| Dismissals | If they said "not interested," respect that permanently |
| Recently released (60 days) | "New" section surfaces fresh content, not the same eternal hits |
| Trending this week | "Trending" section reflects what people are watching NOW |

The result is a short, focused list where every item is relevant — not a Netflix-style infinite scroll of everything ever made. Less content shown = higher signal-to-noise = more likely the user finds something to watch.

## Architectural implication

Every new data source or section added to the home page should pass through the full preference filter chain: providers → genres → dismissals. The `ApplyPreferenceFilters` helper in `SearchEndpoints.cs` and `DbService.UserPreferences` exist for this reason — any new endpoint that serves home page content should use them.

The tabbed TV/Movies split exists because the user's intent is different: "I want a show to binge" vs "I want a movie for tonight." Mixing them dilutes both.
