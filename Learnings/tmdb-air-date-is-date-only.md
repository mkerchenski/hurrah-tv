# TMDb `air_date` Is Date-Only and Lands at Midnight UTC

> **Area:** TMDb | Data | Date
> **Date:** 2026-05-28
> **Resolves:** mkerchenski/hurrah-tv#145

## Context

During the PR #156 review for the Watching-status override (#145), GitHub Copilot caught a same-day-trip bug in the staleness comparison that none of the four hurrah-tv reviewer agents — or my own pass — surfaced. The original code:

```csharp
// commit e2a1154 (before the fix):
bool overrideLatestWatched = isWatching
    && item.LatestEpisodeDate is { } led
    && (todayUtc - led).TotalHours > 18;
```

reads as "bypass the gate if more than 18 wall-clock hours have passed since the episode aired." That mental model is wrong, because TMDb's `air_date` field is **date-only** (`"YYYY-MM-DD"`) and Dapper / `System.Text.Json` deserializes it as midnight UTC of that date.

Concrete failure: user watches today's Kimmel at 10am ET (14:00 UTC). `IsLatestEpisodeWatched = true`. They open Home at 7pm ET (23:00 UTC) **the same day**. The math:

```
(23:00 UTC today - 00:00 UTC today).TotalHours = 23h
23 > 18 → bypass fires → caught-up show resurfaces in Available Now
```

The user just watched today's episode 5 hours ago and TMDb hasn't published anything newer. The row appearing is a regression, not a feature.

## Learning

When doing time math against a TMDb `air_date` (or anything else deserialized from a date-only field), treat it as **date-only semantics**, not a wall-clock timestamp:

1. **Date-only is the correct mental model.** TMDb returns `air_date: "2026-05-28"` with no hour-of-day. Any hour-precision math against it is computing differences from midnight UTC, not from when the episode actually aired (which the API doesn't tell you).
2. **Prefer calendar-day comparison.** `led.Date < today` reads as its own intent ("a previous calendar day"), no magic number, no hour-of-day surprise. Pairs with the broader typed-comparison principle in [[date-predicates-prefer-typed-comparisons]] — both are about avoiding silent sign/precision drift from comparisons that look correct.
3. **If hour-precision really is required**, normalize first. If a particular show's airing time matters (e.g., "12 hours after the episode actually aired"), add an assumed airing time to the date: `led.Date.AddHours(20)` to assume an 8pm UTC airing. But this is rarely the right tool — calendar-day rules are usually closer to user intent and don't depend on hidden assumptions.

## Example

The fix in `HurrahTv.Shared/Filters/WatchlistFilters.cs` (PR #156 commit `6b1b3b3`):

```csharp
// for Watching, bypass IsLatestEpisodeWatched once the calendar day has
// advanced past LatestEpisodeDate.Date — TMDb's air_date is date-only and
// parsed as midnight UTC, so an hour-based threshold could trip the SAME
// day the user marked the episode watched and resurface a caught-up show.
bool overrideLatestWatched = isWatching
    && item.LatestEpisodeDate is { } led
    && led.Date < today;
```

Where `today = todayUtc.Date` (computed earlier in the method).

## Test boundary the calendar-day rule pins

| Scenario | `led.Date` | `today` | `led.Date < today` | Outcome |
|---|---|---|---|---|
| User watched today's episode | today | today | false | gate fires → hidden (correct, no new episode yet) |
| User watched yesterday's episode | yesterday | today | true | bypass fires → visible (correct, new episode plausibly available) |
| Weekly show, watched Sunday, today is Monday | Sun | Mon | true | bypass fires → visible (acceptable false-positive per the plan) |

The "today" + "yesterday" pair of tests catches both `<` → `<=` (would let same-day bypass) and `<` → `>` (would block yesterday's bypass) mutations without needing artificial hour boundaries.

## Anti-pattern

Reading TMDb's `air_date` as a wall-clock timestamp — phrases like "the episode aired at X o'clock UTC" or "12 hours after airing" — when the field doesn't contain hour-of-airing information. Any such inference is computed from midnight UTC of the date, not from when the show actually went out. Bugs in this shape are subtle: the code reads sensibly, passes typed-comparison sniff tests, and only fails when the wall-clock hour crosses the chosen threshold *within the same calendar day as the air_date*.

## How to spot it in review

- Any `TimeSpan` subtraction or `TotalHours` / `TotalMinutes` math involving a field sourced from a TMDb date (`air_date`, `release_date`, `first_air_date`, `last_episode_to_air.air_date`, etc.).
- Hour-based thresholds (`> 18`, `>= 24`) anchored to a date-only field — the threshold likely models a wall-clock relationship that the underlying data can't actually express.
- Tests that anchor `latestEpisode` to `Today.AddHours(N)` for a fixed-midnight `Today`. These can pass in test while the production scenario (where `todayUtc` is the current moment) crosses the threshold during the same calendar day.
