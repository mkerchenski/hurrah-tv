# WASM Date Math: The Right Answer Depends on Where the DateTime Came From

> **Area:** WASM | Data
> **Date:** 2026-04-16

## Context
`blazor-wasm-datetime.md` established "use `DateTime.Now` for display-facing date labels in WASM" because TMDb returns calendar-only strings like `"2026-03-28"` that parse to `DateTime.Kind = Unspecified`. Shipping a `ShouldShowEpisodeWatched` predicate comparing `DateTime.Now.Date` against `item.LatestEpisodeDate.Date` re-introduced the same off-by-one class of bug the earlier learning had solved — but the fix went the *opposite* direction. The reason: `LatestEpisodeDate` does not come from TMDb. It comes from Postgres as `TIMESTAMPTZ`.

## Learning
**The `DateTime.Kind` of a WASM-side date depends on its provenance, and the correct comparison baseline depends on the `Kind`:**

| Source | Kind in WASM | Correct comparison baseline |
|---|---|---|
| TMDb date-only string `"2026-03-28"` | `Unspecified` (treat as local calendar) | `DateTime.Now.Date` |
| Postgres `TIMESTAMPTZ` via Npgsql → JSON → STJ | `Utc` (or offset-aware) | `DateTime.UtcNow.Date`, with the other operand normalized via `.ToUniversalTime()` |
| Server-stamped `DateTime.UtcNow` round-tripping | `Utc` | `DateTime.UtcNow` |

The trap is that both operands *look* like "just a DateTime" in the call site. The `.Date` truncation silently mixes local and UTC dates when the Kinds differ — the result is a `TimeSpan` where the `.TotalDays` can land negative near midnight even when both calendar dates are "today" in real life. For a US West Coast user at 8am local, an episode stored as `2026-04-16 06:00Z` has `.Date == 2026-04-16` in UTC but the user's `.Now.Date == 2026-04-15`; the subtraction gives `-1d` and the gate hides the control for up to 8 hours.

**Rule when computing a `(nowDate - otherDate).TotalDays` span:** normalize both operands to the same timezone explicitly. Don't assume the DB and the runtime agree.

## Example
```csharp
// TMDb air date (string "2026-04-16" → Unspecified): local comparison is correct
int daysAgo = (int)(DateTime.Now.Date - airedFromTmdb.Date).TotalDays;

// Postgres TIMESTAMPTZ (Utc Kind): normalize BOTH sides to UTC
int days = (int)(DateTime.UtcNow.Date - item.LatestEpisodeDate.ToUniversalTime().Date).TotalDays;

// The bug both learnings warn against — mixing kinds:
// int days = (int)(DateTime.Now.Date - item.LatestEpisodeDate.Date).TotalDays;  // BAD
```

## How to spot it in review
- Any `.Date` on one operand but not the other.
- Any `.TotalDays` result that can be negative when the user intuitively expects it to be ≥ 0.
- Any `DateTime` field populated by the server/DB being compared against `DateTime.Now` on the client.

The first place it usually surfaces is "feature works locally, a user reports it's broken after 4pm" — the UTC boundary just crossed their local midnight.
