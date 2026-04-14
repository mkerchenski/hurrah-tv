# TMDb Season 0 Means Specials — Not "No Data"

> **Area:** TMDb
> **Date:** 2026-04-14

## Context
Added `LatestEpisodeSeason` and `LatestEpisodeNumber` columns to `QueueItems` to track which specific episode was last aired. When these columns aren't yet populated (new columns, not yet refreshed from TMDb), we needed a way to distinguish "data is absent" from "data says Season 0". Used `== 0` as a sentinel for missing data. This created an infinite close/reload loop for certain shows.

## Learning
**TMDb uses Season 0 as the official season number for "Specials"** — bonus episodes, OVAs, behind-the-scenes, etc. A show where the latest episode is a Special will legitimately return `season_number = 0` from `last_episode_to_air`. This is not an error or a null value.

Using `== 0` as a "data not populated" sentinel therefore breaks for any show where the latest content is actually a Special — the code treats valid Season 0 data as missing data and re-triggers the data fetch indefinitely.

**Always use `HasValue` (nullable check) as the test for absent data**, not a magic integer value:

```csharp
// BAD — 0 is a valid TMDb season number (Specials)
bool dataAbsent = item.LatestEpisodeSeason == 0;

// GOOD — null means not yet populated
bool dataAbsent = !item.LatestEpisodeSeason.HasValue;
```

The same applies to `LatestEpisodeNumber` — though episode 0 is rarer, episode 1 within Season 0 is common. Keep both as `int?` and test `HasValue` throughout.

## Affected Show Types
- Anime with OVA collections (Season 0 is extremely common)
- Long-running shows with special episodes between seasons
- US network shows with "Making of" or "Recap" specials
- Any show TMDb classifies as having a standalone Specials season
