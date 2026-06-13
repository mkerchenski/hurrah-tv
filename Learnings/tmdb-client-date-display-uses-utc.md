# Client TMDb Date Display Uses a UTC "Today" (Deliberate Reversal)

> **Area:** WASM | Data | UI
> **Date:** 2026-06-13
> **Resolves:** mkerchenski/hurrah-tv#192

## Context

Two earlier learnings — [[blazor-wasm-datetime]] and [[wasm-datetime-source-matters]] — established that TMDb date-only strings (`"2026-06-11"`, `Kind=Unspecified`) should be compared against **`DateTime.Now.Date`** (the user's local calendar day) for *display*, so "aired yesterday" / "Today" badges match what day the user thinks it is. That was the right call for isolated badges.

But the app grew a second surface that classifies the *same* TMDb dates: the Home page's "Available Now / Upcoming" rows, which are computed **server-side in UTC** (`WatchlistFilters`, `todayUtc`) so the result is cacheable and identical for every client. Meanwhile `EpisodeBrowser` (aired/upcoming episode split) used local `DateTime.Now.Date` and `Details` used `DateTime.UtcNow` — so the two client surfaces could disagree with each other *and* with Home about whether an episode had "aired."

#190 had just made the parse deterministic (`TmdbDate.TryParse` → `Kind=Utc`); #192 was the comparison/display follow-up.

## Learning

**Hurrah.tv standardizes client-side TMDb date *display* on a UTC "today" (`DateTime.UtcNow.Date`), deliberately overriding the local-baseline rule from the two prior learnings.** The driver is **cross-surface consistency**: EpisodeBrowser, Details, and the Home rows now all answer "has this aired / is this upcoming?" against the same UTC calendar day the server uses. One self-consistent app beat each surface being individually "most local-accurate."

This is centralized in **`HurrahTv.Shared/Models/TmdbDateDisplay.cs`** (`IsRecent` / `FormatRelative` / `FormatAbsolute` / `SplitAiredUpcoming`), which takes an injected `todayUtc` and uses **typed `DateTime` comparisons**, never a signed day-diff integer (the [[date-predicates-prefer-typed-comparisons]] anti-pattern — that's how `IsRecent` used to report future dates as "recent" and `FormatAirDate` truncated a near-future date to "today"). Callers pass `DateTime.UtcNow.Date`.

**Rejected alternative:** keep local `DateTime.Now.Date` everywhere and instead flip Home/server to compare per-request in the caller's timezone. Rejected because server classification is shared + cached across clients and has no single "local" to use — UTC is the only stable baseline there, so the client was the cheaper thing to align.

**Known tradeoff (accepted):** for a user west of UTC in the evening, once UTC rolls past local midnight, an episode whose `air_date` is the user's *local tomorrow* can briefly show as already aired (and vice-versa for users east of UTC in the morning). This is the exact off-by-one the prior learnings avoided — accepted here in exchange for whole-app consistency. The user base is primarily US; revisit if that changes.

## Example

```csharp
// HurrahTv.Shared/Models/TmdbDateDisplay.cs — typed, UTC-baseline, injectable today
public static string FormatRelative(string? raw, DateTime todayUtc)
{
    if (!TmdbDate.TryParse(raw, out DateTime date)) return "";
    int dayDelta = (date.Date - todayUtc.Date).Days; // sign-correct: a future date can't truncate to 0
    return dayDelta switch { 0 => "today", 1 => "tomorrow", /* … */ _ => date.ToString("MMM d, yyyy") };
}

// Call sites (EpisodeBrowser.razor, Details.razor):
TmdbDateDisplay.FormatRelative(_details.NextEpisodeAirDate, DateTime.UtcNow.Date);
```

## How to spot it in review

- A new client surface rendering TMDb air dates with `DateTime.Now` — for *this app* that now diverges from Home. Route it through `TmdbDateDisplay` with `DateTime.UtcNow.Date` instead.
- Don't "fix" `TmdbDateDisplay` back to local on the strength of the two older learnings alone — this learning supersedes them for the cross-surface display case.
