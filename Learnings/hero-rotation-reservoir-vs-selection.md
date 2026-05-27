# Rotating AI Hero: Split the Reservoir from the Selection

> **Area:** API | AI | UI
> **Date:** 2026-05-27

## Context
The Home "Curated for You" grid felt frozen: the same picks showed for weeks. Root cause wasn't (only) the prompt — it was the architecture. `CurationCache` invalidated solely on a watchlist-hash change, and the hero was literally `_curatedItems.FirstOrDefault(...)` — always pinned to the AI's #1. Even a perfect prompt would have shown the same thing every day. (#135)

## Learning

Separate two concerns that the old design fused into one cache:

- **Reservoir** — the expensive, AI-scored candidate set. Regenerate it *periodically* (watchlist change **OR** `GeneratedAt` older than N days — the column already existed but nothing read it) and make it *large* (~25–30 scored picks, not 12–15) so there's material to rotate through.
- **Selection** — which reservoir item is the hero *right now*. This is cheap, pure, and runs every page load. It costs **zero** AI. Rotation and the no-repeat cooldown live here, not in the reservoir.

This is the key inversion: **rotation is a read-time concern; freshness of material is a periodic concern.** Conflating them ("regenerate only when the watchlist changes") is what froze it.

### The within-day-stability trick

A daily-rotating, deterministic hero needs to be *stable within a calendar day* (a page refresh must not reshuffle it) yet *advance* at the day boundary. The naive "best-eligible not shown in the last 14 days" breaks this: the moment you record today's pick as an impression, it's "shown today" and the next refresh picks something else.

Fix: treat **shown-today as still eligible**.

```csharp
if (shownDay == today) return keepTodaysPickEligible; // recording today's pick doesn't evict it today
return (today - shownDay).Days > cooldownDays;
```

So recording the impression is idempotent for the rest of the day, and the title only enters the cooldown window tomorrow. A manual "shuffle" passes `keepTodaysPickEligible: false` to advance past today's pick on demand. The boundary is UTC midnight (server `DateTime.UtcNow.Date`) — acceptable for a once-a-day pick; documented rather than localized.

### Cost shape

Reservoir 15→30 picks is a few hundred extra Haiku output tokens (negligible). The only real new spend is the time-based regen (≤1 paid call/user/N days), gated by the existing budget check. The daily rotation itself is free because it's pure selection over already-cached data.

### Testability

The selection rule is pure and lives in `HurrahTv.Shared/Curation/HeroSelector.cs` with `DateTime todayUtc` injected — so cooldown fences (exactly-N-days-ago is still excluded; N+1 is eligible), within-day stability, daily advance, and the thin-reservoir fallback (everything in cooldown → least-recently-shown) are all unit-testable at exact day boundaries. Stale cache rows from before scores existed deserialize with score 0 and self-heal at the next 7-day regen.

## File pointers
- `HurrahTv.Shared/Curation/HeroSelector.cs` — pure selection + cooldown
- `HurrahTv.Api/Services/CurationService.cs` — `GetCuratedHeroAsync`, `ReservoirMaxAgeDays`, the 7-day freshness clause in `GetCuratedRowsAsync`
- `HurrahTv.Api/Services/DbService.cs` — `CurationHeroImpressions` table, `GetHeroImpressionsAsync` / `RecordHeroImpressionAsync`
