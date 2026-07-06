# Dapper + Npgsql: a Postgres `DATE` Column Round-Trips Through `DateOnly`, Not `DateTime`

> **Area:** Data
> **Date:** 2026-07-06
> **Resolves:** mkerchenski/hurrah-tv#229

## Context
Added the first true `DATE` column in the schema — `CurationDailyHero.ForDate` (the UTC calendar day a persisted hero is valid for). Every other date column in the codebase is `TIMESTAMPTZ`, which Npgsql maps to `DateTime` and Dapper handles without a thought. The `DATE` column failed twice in a row on the round-trip, each time with a *different* error.

## Learning
Modern Npgsql (7+) maps a bare Postgres `date` to **`DateOnly`**, not `DateTime`. That breaks Dapper in **both** directions, and the two failures look unrelated:

1. **Read** — mapping a `date` column into a tuple/POCO field typed `DateTime` throws `System.InvalidCastException: Object must implement IConvertible`. Npgsql hands Dapper a `DateOnly`; Dapper falls back to `Convert.ChangeType(DateOnly, typeof(DateTime))`, and `DateOnly` doesn't implement `IConvertible`.
2. **Write** — passing a `DateOnly` parameter throws in Dapper's `CreateParamInfoGenerator` — this Dapper version's parameter generator can't bind `DateOnly`. (A `DateTime` parameter binds fine and Postgres casts it into the `date` column.)

The stable recipe — keep a clean `DateOnly` public contract (timezone-safe, no `DateTimeKind`/midnight ambiguity) but bridge both edges to `DateTime` for Dapper:

- **Read:** `SELECT ForDate::timestamp AS ForDate` so Npgsql yields a `DateTime`; map to a `DateTime` tuple field, then wrap: `DateOnly.FromDateTime(row.ForDate)`.
- **Write:** pass `forDate.ToDateTime(TimeOnly.MinValue)` (a midnight `DateTime`) as the parameter; Postgres truncates it into the `date` column.

Why `DateOnly` at the API boundary is worth the bridging: a "which calendar day" value has no time-of-day, so `DateOnly` makes the freshness comparison (`forDate == today`) unambiguous and testable at the UTC day boundary — no `.Date` juggling, no `DateTimeKind` traps.

## Example
```csharp
// read — cast to timestamp so Npgsql returns DateTime, then wrap to DateOnly for the caller
public async Task<(string heroJson, DateOnly forDate, string watchlistHash, int tmdbId)?> GetDailyHeroAsync(string userId, string mediaType, CancellationToken ct = default)
{
    using NpgsqlConnection db = await OpenAsync(ct);
    CommandDefinition cmd = new(
        "SELECT HeroJson, ForDate::timestamp AS ForDate, WatchlistHash, TmdbId FROM CurationDailyHero WHERE UserId = @UserId AND MediaType = @MediaType",
        new { UserId = userId, MediaType = mediaType }, cancellationToken: ct);
    (string HeroJson, DateTime ForDate, string WatchlistHash, int TmdbId)? row =
        await db.QuerySingleOrDefaultAsync<(string, DateTime, string, int)?>(cmd);
    return row is null ? null : (row.Value.HeroJson, DateOnly.FromDateTime(row.Value.ForDate), row.Value.WatchlistHash, row.Value.TmdbId);
}

// write — pass a midnight DateTime; Dapper can't bind a DateOnly param
new { ForDate = forDate.ToDateTime(TimeOnly.MinValue), /* … */ }
```

The `DATE` column is the trigger — a `TIMESTAMPTZ` column would never hit either failure. If you reach for `DATE` (correct when the value truly has no time-of-day), budget for this bridging or the round-trip test fails on the very first run.
