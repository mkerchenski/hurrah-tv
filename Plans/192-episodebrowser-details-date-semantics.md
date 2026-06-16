# Align EpisodeBrowser/Details Date Semantics with the UTC Convention — Implementation Plan

> **Status:** Draft
> **Phase:** 2 (post-#190 follow-up)
> **Tracking issue:** mkerchenski/hurrah-tv#192

## Context

#190 unified the TMDb date-only **parse** (`TmdbDate.TryParse` → `Kind=Utc`). #192 covers the **comparison/display** side, which still has three defects in the Client:

1. **Inconsistent "today" baseline.** `EpisodeBrowser.razor:72` uses `DateTime.Now.Date` (local); `Details.razor` uses `DateTime.UtcNow`. **Decision (Mike, this session): standardize on UTC** for cross-surface consistency with the Home page's server-side UTC classification — even though `blazor-wasm-datetime.md` / `wasm-datetime-source-matters.md` currently document *local* as correct for TMDb display dates. Those learnings will be updated (Phase 3). Known tradeoff: a US-west user in the evening can briefly see an episode airing their local *tomorrow* as already aired once UTC rolls over.
2. **Signed day-diff anti-pattern** (`Details.razor:640`, `Learnings/date-predicates-prefer-typed-comparisons.md`) with two reachable bugs:
   - **2a** `IsRecent` returns true for *future* dates (negative `TotalDays` still `<= 30`) — hits the movie `IsRecent(ReleaseDate)` path at `Details.razor:284`, so an unreleased movie shows "New release."
   - **2b** `FormatAirDate` labels a future date as "today" because `(int)` truncates the negative fraction toward zero — hits `FormatAirDate(NextEpisodeAirDate)` at `Details.razor:232`.
3. **Double-parse:** `EpisodeBrowser.razor:73-75` parses each episode's `AirDate` twice (`HasAired` then `IsFuture`).

**Outcome:** one tested, pure Shared helper owns TMDb date classification + relative formatting with sign-correct typed comparisons; both Client components route through it on a UTC baseline; the contradicted learnings are corrected.

## Approach

Extract the pure logic into `HurrahTv.Shared` (per CLAUDE.md "factor for testability" — mirrors `WatchlistFilters.Apply` taking an injected `DateTime todayUtc`), unit-test it, then thin the two Razor components down to calls.

### Phase 1 — Shared helper + tests (pure logic)

**New `HurrahTv.Shared/Models/TmdbDateDisplay.cs`** — static, depends only on `TmdbDate.TryParse` + `EpisodeInfo` (already in `Shared/Models/ShowDetails.cs`). All methods take `DateTime todayUtc` for testability:

- `bool IsRecent(string? raw, DateTime todayUtc, int withinDays = 30)` — typed: `dayDelta <= 0 && dayDelta >= -withinDays` (excludes future → fixes **2a**).
- `string FormatRelative(string? raw, DateTime todayUtc)` — `int dayDelta = (date.Date - todayUtc.Date).Days` (sign-correct, no truncation → fixes **2b**), then `switch`: `0→"today"`, `1→"tomorrow"`, `2..7→"in N days"`, `>7→absolute`, `-1→"yesterday"`, `-2..-7→"N days ago"`, `-8..-30→"N/7 weeks ago"`, `<-30→absolute`. Unparseable → `""`. Preserves existing `FormatAirDate` phrasing.
- `string FormatAbsolute(string? raw)` — `"MMM d, yyyy"` or `""` (replaces `EpisodeBrowser.FormatDate`; the `"0000-00-00"` guard is subsumed because `TryParse` fails on it).
- `(IReadOnlyList<EpisodeInfo> Aired, EpisodeInfo? NextUp) SplitAiredUpcoming(IReadOnlyList<EpisodeInfo> episodes, DateTime todayUtc)` — single pass, parses each `AirDate` **once** (→ fixes **3**): `date.Date <= todayUtc.Date` → aired; else track lowest-episode-number future as `NextUp`; unparseable dropped from both (matches current behavior).

**New `HurrahTv.Shared.Tests/TmdbDateDisplayTests.cs`** — plain `Assert`, helpers inline, pass fixed `todayUtc`. Pin: **2a** (future `IsRecent` → false; unreleased-movie case), **2b** (`todayUtc.AddDays(1)` → "tomorrow", not "today"), FormatRelative arms (yesterday/N days ago/weeks ago/absolute past+future), `SplitAiredUpcoming` (today=aired, lowest future ep = NextUp, unparseable dropped, single-parse). Reference `#192` in test names.

**Tests:** required (new Shared pure logic). **Verify:** `dotnet test HurrahTv.Shared.Tests` green; format gate clean.

### Phase 2 — Wire Client to the helper (UTC baseline)

- **`EpisodeBrowser.razor`**: line 72 → `DateTime today = DateTime.UtcNow.Date;`; replace the inline `Where(HasAired)`/`Where(IsFuture)` block with `var (aired, nextUp) = TmdbDateDisplay.SplitAiredUpcoming(_seasonDetail.Episodes, today);`; `@FormatDate(ep.AirDate)` → `@TmdbDateDisplay.FormatAbsolute(ep.AirDate)`. **Delete** local `HasAired`/`IsFuture`/`FormatDate`.
- **`Details.razor`**: `IsRecent(x)` → `TmdbDateDisplay.IsRecent(x, DateTime.UtcNow.Date)`; `FormatAirDate(x)` → `TmdbDateDisplay.FormatRelative(x, DateTime.UtcNow.Date)` (5 call sites: 232, 249, 282 + IsRecent at 237, 284). **Delete** local `IsRecent`/`FormatAirDate`.

**Tests:** none (Razor — verify in browser per CLAUDE.md). **Verify:** `dotnet build` clean ×3; browser smoke — EpisodeBrowser aired/upcoming split + Details "Next episode / New episode / Last aired / New release" labels, ideally checked near midnight UTC.

### Phase 3 — Record the decision (learnings)

Run `/compound`: write one new learning — *"Hurrah.tv standardizes client TMDb date display on UTC for cross-surface consistency (#192)"* — capturing the decision, the rejected local alternative, and the US-west evening tradeoff. Add a one-line `> Superseded for TMDb client-display dates by [[...]] (#192)` pointer at the top of `blazor-wasm-datetime.md` and `wasm-datetime-source-matters.md` (preserve their history; don't silently flip).

**Verify:** new learning present; both pointers added.

---

## Affected Projects

| Project | Touched | Notes |
|---|---|---|
| HurrahTv.Api | no | — |
| HurrahTv.Client | yes | `EpisodeBrowser.razor`, `Details.razor` — thin to helper calls, UTC baseline |
| HurrahTv.Shared | yes | new `Models/TmdbDateDisplay.cs` (additive; no DTO/contract change) + `Tests/TmdbDateDisplayTests.cs` |

## DB Schema Changes
None.

## Blazor WASM Considerations
- Pure presentation refactor — no lifecycle, DI, or render-mode change. No new disposables.
- `EpisodeInfo` DTO unchanged; helper is additive, so no Client/Api ripple.
- Single-threaded WASM: helpers are synchronous and pure; no threading concern.

## External Integrations
None directly. Inputs are already-fetched TMDb date strings; no new TMDb/Anthropic/Twilio calls.

## Follow-on actions
- Phase 3 *is* the `/compound` step (decision reversal must be recorded).
- No `CurationCache` impact.

## Verification (end-to-end)
1. `dotnet test HurrahTv.slnx` — new `TmdbDateDisplayTests` green, full suite green.
2. `dotnet format --verify-no-changes --severity info --no-restore HurrahTv.slnx` — clean (CI gate).
3. `dotnet build HurrahTv.slnx` — 0 warnings across all three projects.
4. Browser: open a TV show with aired + upcoming episodes → EpisodeBrowser splits correctly, "Upcoming" badge on the single next episode; Details shows "Next episode — tomorrow/in N days" (not "today" for a near-future ep) and an unreleased movie does **not** show "New release".
