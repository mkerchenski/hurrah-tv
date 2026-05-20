# Date Predicates: Compare DateTime Directly, Not Day-Diff Integers

> **Area:** Data | WASM
> **Date:** 2026-05-20

## Context

Issue #79 surfaced a bug where TV shows silently vanished from both top home rows. The home page's "Continue Watching" predicate used:

```csharp
i.DaysSinceLatestEpisode(todayUtc) is <= 7
```

`DaysSinceLatestEpisode` returns an `int?`. If a TMDb edge case ever stamps `LatestEpisodeDate` slightly in the future (e.g. a backfill race), the helper returns a **negative** integer — and the `<= 7` pattern accepts it. The unaired episode silently classifies as "already aired" and lands in the Continue Watching row instead of Upcoming, or worse, falls through both predicates depending on the rest of the chain.

Same class of bug bit Upcoming Episodes (`DaysUntilNextEpisode in [0,14]` accepts the briefly-negative case near airtime — see #49/#70 for the original incident).

## Learning

**When a predicate gates UI state on a date window, compare the underlying `DateTime` values directly. Reducing to a signed integer first hides the sign edge case.**

| Pattern | Risk |
|---|---|
| `DaysSinceLatest is <= 7` | Accepts negative ints silently → future date treated as "already aired" |
| `DaysUntilNext is >= 0 and <= 14` | Accepts a briefly-stale stamp between airtime and TMDb refresh as "upcoming" |
| `latest.Date <= today` | Sign-correct: a future date fails the comparison by construction |
| `next.Date > today && next.Date <= windowEnd` | Sign-correct: strict future, explicit upper bound |

Day-diff helpers are still useful for **display** (rendering "3d ago" / "in 2d" badges) — but the badge formatter already collapses negatives to empty string. The mistake is using the same integer in a **predicate**, where the sign matters but the comparison hides it.

## Rule of thumb

- **Predicates that decide which row/state an item belongs to** → compare `DateTime` directly with `<` / `<=` / `>` / `>=` against `todayUtc.Date` (or `todayUtc.AddDays(windowDays)`).
- **Display formatters that produce human-readable strings** → reduce to a day-diff integer and pattern-match, but always include a `null or < 0 => ""` guard at the top of the switch.

## How to spot it in review

- Any predicate using `is <= N` or `is >= N and <= M` against a `Days*` helper that returns `int?`. The `null` arm is explicit but the **negative** arm is silent.
- Any filter logic that mixes "days since" and "days until" with overlapping integer ranges (e.g. `<= 7` and `>= 0`) — the seam is where items vanish or double-count.
- See `WatchlistFilters.Apply` in `HurrahTv.Shared/Filters/` for the typed-comparison version that replaced the integer-window approach.
