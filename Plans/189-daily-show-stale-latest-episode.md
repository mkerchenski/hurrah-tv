# Daily shows: stale latest-episode fields → wrong "X days ago" + watched episode stays in Available Now — Implementation Plan

> **Status:** Draft
> **Phase:** 2 (maintenance / bug)
> **Tracking issue:** mkerchenski/hurrah-tv#189

## Context

For a daily show (repro: *Late Night with Seth Meyers*) the stored `QueueItem` latest-episode fields lag the truly-newest aired episode, producing two linked symptoms:

1. **Stale "X days ago" in Available Now.** Home → Available Now shows "4 days ago"; Details → episode browser shows an episode that aired 1 day ago. The newer episode never becomes the displayed "latest."
2. **Watched-but-still-in-Available-Now.** Marking that 1-day-ago episode watched does not remove the show from Available Now — it stays, labeled "4 days ago."

**Root cause (two distinct mechanisms, one per symptom):**

- The latest-episode fields are sourced from TMDb's `last_episode_to_air` in `TmdbService.GetEpisodeDatesAsync` (`HurrahTv.Api/Services/TmdbService.cs:488`), refreshed only when `LastEpisodeCheckAt > EpisodeCheckStaleAfter (12h)` and capped at `Take(10)` per queue load (`QueueEndpoints.cs:164`). TMDb's own `last_episode_to_air` ingestion also lags. Meanwhile the Details episode browser reads **live season episodes** (`GetSeasonAsync`), which are current — hence the "4 days" vs "1 day" mismatch. → **symptom 1.**
- `IsLatestEpisodeWatched` is computed client-side in `Home.razor:443` as `_watchedSet.Contains((TmdbId, LatestEpisodeSeason, LatestEpisodeNumber))` — an **exact match against the stale stored S/E**. Watching the truly-newest episode (S, E2) never flips the flag for the stored older "latest" (S, E1), so `WatchlistFilters.cs:84` (`!item.IsLatestEpisodeWatched`) keeps the show in Available Now. → **symptom 2.**

**Intended outcome:** the Available Now date matches what Details shows, and marking the newest aired episode watched removes the show from Available Now — even when the stored `LatestEpisode*` fields haven't refreshed yet.

**Interacts with:** #172/#173 (don't resurface caught-up daily show until next airs) and #168/#177 (show "Episode Watched" for any aired latest). Both assume the stored fields are current; this issue is that assumption breaking. The `overrideLatestWatched` resurface predicate (`WatchlistFilters.cs:79-81`, keyed on `NextEpisodeDate.Date < today` per [[state-gate-override-needs-positive-signal]]) is **kept unchanged** — it still correctly resurfaces when a genuinely newer episode aired.

## Affected Projects

| Project              | Touched | Notes                                                                 |
|----------------------|---------|-----------------------------------------------------------------------|
| HurrahTv.Shared      | yes     | New pure reconciliation helper + regression test. No DTO shape change. |
| HurrahTv.Client      | yes     | `Home.razor` `StampWatchedFlags` uses the new helper.                  |
| HurrahTv.Api         | yes     | `TmdbService.GetEpisodeDatesAsync` augments latest from live season.   |

**DB schema:** no change — `LatestEpisode*` columns already exist. No migration.
**API contract:** no change — `QueueItem` shape unchanged; Phase 1 adds a static helper only.

---

## Phase 1 — Caught-up reconciliation (fixes symptom 2) — *Shared + Client*

Make the caught-up determination robust to a stale stored latest: a Watching show is caught up if the user has watched the stored latest episode **or anything newer** (high-water-mark), instead of requiring an exact match on the stale stored S/E.

1. **`HurrahTv.Shared/Models/QueueItemExtensions.cs`** — add a pure helper:
   ```csharp
   // caught up if the user's newest-watched episode for this show is >= the stored
   // "latest" (lexicographic by season then episode). Robust to a stale stored S/E:
   // watching a genuinely-newer episode (S,E2) counts as caught up even when the
   // stored latest still lags at (S,E1). pins #189.
   public static bool IsLatestEpisodeWatched(
       int? latestSeason, int? latestEpisode, (int Season, int Episode)? highWaterWatched)
   ```
   Returns `false` when `latestSeason`/`latestEpisode` is null (no notion of latest — preserves current behavior) or when `highWaterWatched` is null. Otherwise `true` iff `highWaterWatched >= (latestSeason, latestEpisode)` compared as a tuple. No `DateTime` involved — comparison is on S/E integers, so no time injection needed.

2. **`HurrahTv.Client/Pages/Home.razor` `StampWatchedFlags` (line ~438)** — replace the exact-match `Contains` with the high-water-mark helper. Pre-compute a per-`TmdbId` max-watched `(Season, Episode)` dictionary **once** from `_watchedSet` before the loop (per CLAUDE.md: never compute per-item inside the loop), then call the helper per item:
   ```csharp
   Dictionary<int, (int Season, int Episode)> highWater = _watchedSet
       .GroupBy(w => w.TmdbId)
       .ToDictionary(g => g.Key, g => g.Max(w => (w.Season, w.Episode)));
   foreach (QueueItem item in _allItems)
       item.IsLatestEpisodeWatched = QueueItemExtensions.IsLatestEpisodeWatched(
           item.LatestEpisodeSeason, item.LatestEpisodeNumber,
           highWater.TryGetValue(item.TmdbId, out var hw) ? hw : null);
   ```
   `WatchlistFilters.cs` is **unchanged** — it already reads `IsLatestEpisodeWatched`; we only make that flag correct.

3. **Tests (`HurrahTv.Shared.Tests`)** — one named regression test pinning the AC scenario, plus boundary cases:
   - `IsLatestEpisodeWatched_Caught_Up_When_Watched_Newer_Than_Stale_Stored_Latest // pins #189`: stored latest = (S, E1), high-water watched = (S, E2) → `true`.
   - exact match (S,E1)/(S,E1) → `true`; watched older (S,E1) than stored (S,E2) → `false`; cross-season newer (S+1,E1) vs stored (S,E5) → `true`; null stored latest → `false`; null high-water → `false`.

**Verify (Phase 1):** `dotnet test`. Then in-browser: a daily Watching show whose newest aired episode is marked watched drops out of Available Now even before the 12h refresh runs.

---

## Phase 2 — Season-sourced latest (fixes symptom 1) — *Api*

Make the stored `LatestEpisode*` reflect the truly-newest aired episode by reading the same live season data the Details browser uses, so the "X days ago" badge matches Details. Independently committable.

1. **`HurrahTv.Api/Services/TmdbService.cs` `GetEpisodeDatesAsync`** — after parsing `last_episode_to_air` / `next_episode_to_air`, when `lastAired` is **recent** (within ~10 days of `DateTime.UtcNow.Date` — gates the extra call to actively-airing shows the user is Watching, skips dormant ones), fetch the current season via `GetSeasonAsync(tmdbId, lastSeason)` and scan its episodes:
   - parse each `EpisodeInfo.AirDate` (raw TMDb string) with the existing date-only → UTC convention (`CultureInfo.InvariantCulture`, `DateTimeStyles.AssumeUniversal | AdjustToUniversal` — see [[tmdb-air-date-is-date-only]]);
   - take the newest episode whose parsed `AirDate.Date <= today` → if it's newer than `last_episode_to_air`, override `lastAired`/`lastSeason`/`lastEpisode` with it;
   - take the earliest episode whose `AirDate.Date > today` in that season → if present and sooner than `next_episode_to_air`, override `nextAir`/`nextSeason`/`nextEpisode` (keeps the resurface signal consistent with the corrected latest).
   - Fall back to the existing `last_episode_to_air`/`next_episode_to_air` values when the season fetch returns null or yields nothing fresher.
2. **Extract a private `TryParseTmdbDate(string?, out DateTime)`** helper in `TmdbService` and reuse it at both the `last`/`next` parse sites and the new season scan, so the date-only convention is expressed once.
3. Thread the existing `CancellationToken` from `GetEpisodeDatesAsync` into the `GetSeasonAsync` call (note: `GetSeasonAsync` currently takes no token — add an optional `CancellationToken` param, defaulting to `default`, mirroring `GetEpisodeDatesAsync`).

**No change to the fire-and-forget refresh shape** (`RefreshStaleItemsInBackground`) — Phase 2 deliberately does **not** await the refresh on first paint (that would regress #175, "decouple refresh from first paint"). The season-sourced value lands on the next `/api/queue` call, same as today; we're improving *accuracy* of the stored value, not its delivery latency. The `episode-dates:{tmdbId}` cache (6h) and `season:` cache (6h) keep the extra fetch cheap.

**Verify (Phase 2):** with a running Api, load the queue twice for a daily Watching show; after the background refresh, the stored `LatestEpisodeDate` and the "X days ago" badge match the newest episode shown in Details → episode browser (AC#1). No Api.Tests assertion required for the live-TMDb path (network-dependent); validate manually per the project's UI-verification rule. If the existing Api.Tests fake TMDb handler is extended to return a season, add a coverage test that the season-sourced latest overrides a stale `last_episode_to_air`.

---

## API Considerations

- The TMDb season fetch is gated on `lastAired` recency so it only fires for actively-airing shows — bounded extra cost, both `episode-dates` and `season` responses are cached 6h. TMDb rate limits are real (see `TmdbService` cache TTLs).
- Background refresh keeps using `IServiceScopeFactory.CreateAsyncScope()` for fresh transients — unchanged.

## Blazor WASM Considerations

- `StampWatchedFlags` runs on the existing `_allItems` mutation path (`ProcessQueueResponse`, `OnItemUpdated`, `OnEpisodeWatchedChanged`); no new lifecycle/disposal surface. The high-water dict is rebuilt on each stamp — O(watched-set), not per-item.

## Out of scope / follow-ups

- **#176** (refactor Available Now / Upcoming into a sustainable architecture) overlaps this area; this plan is a targeted bug fix and deliberately leaves `WatchlistFilters` structure intact.
- **#175** (decouple refresh from first paint) — Phase 2 is careful not to regress it.
- After landing: `/compound` to capture the season-sourced-latest pattern and the high-water-mark reconciliation as learnings.

## Verification (end-to-end)

1. `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` (the exact CI gate).
2. `dotnet test` — Phase 1 regression + boundary tests green (stop any `dotnet watch` first — see [[dotnet-watch-locks-shared-dll]]).
3. Browser, daily Watching show (e.g. Late Night with Seth Meyers): (a) Available Now "X days ago" matches Details episode browser after refresh; (b) marking the newest aired episode watched removes the show from Available Now immediately, before the 12h refresh.
