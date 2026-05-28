# User Intent Should Override External-Data Gates

> **Area:** UI | Data | Design
> **Date:** 2026-05-28
> **Resolves:** mkerchenski/hurrah-tv#145

## Context

`WatchlistFilters.Apply` (the canonical Home partition in `HurrahTv.Shared`) used to gate every queue item through `IsStreamableOn(userServices)` regardless of status. That predicate checks the item's parsed `AvailableOnJson` against the user's subscribed providers, with the post-#141 carve-out that empty provider data is "don't hide" rather than "hide".

The carve-out covered the case where TMDb hadn't *yet* populated provider data (stale-refresh window). It didn't cover the case where TMDb *permanently* has nothing (see [[tmdb-providers-empty]] for the four classes). For Kimmel-class shows on Hulu, `availableonjson` is permanently `[]` after a fresh refresh, and a similar `IsLatestEpisodeWatched` gate would hide the show all day after the user marked yesterday's episode watched, because TMDb's episode-data lag meant `LatestEpisodeDate` hadn't yet advanced.

Net effect: a user who'd explicitly added a show to their queue *and* set its status to `Watching` could see it disappear from Home Available Now because two independent external-data signals (provider list, episode date) both came up dry.

## Learning

When the user has explicitly committed to something — opted in, said "yes I want this", flagged active intent — that signal should generally beat external data that contradicts or fails to confirm the commitment. The gates exist to filter *unselected* noise out of a surface; for *selected* items the gates often become false negatives.

Concretely, for any filter that gates an active-user surface on external/cached/derived data, ask:

1. **What does the user already told us?** Status flags, subscriptions, dismissals, recent actions.
2. **Is the gate trying to keep noise out, or is it trying to verify a positive claim?** Noise-out is fine for unselected items; positive-verification often misfires on incomplete external data.
3. **What does the user lose if the gate fires a false negative?** If they lose visibility on something they actively committed to, that's worse than a small amount of noise.

Implement the override as a *status-aware bypass* of the gate, not a removal of the gate. Non-active statuses keep the strict rule (the noise filter is still useful there); active statuses bypass it.

## Example

The override in `HurrahTv.Shared/Filters/WatchlistFilters.cs` (post-#145):

```csharp
bool isStreamable = item.IsStreamableOn(userServices);
bool isWatching = item.Status == QueueStatus.Watching;

// Available Now: Watching status bypasses the streamability gate entirely.
// Non-Watching items keep the strict gate so the row stays "things you can
// actually watch" (the noise filter is still useful for WantToWatch / Finished).
if (isWatching || isStreamable)
{
    // for Watching, additionally bypass IsLatestEpisodeWatched once the calendar
    // day has advanced past LatestEpisodeDate — TMDb's episode-data lag is
    // another external-data gap that hides committed shows. Calendar-day rather
    // than hour-based because TMDb's air_date is date-only — see
    // [[tmdb-air-date-is-date-only]] for the same-day-trip Copilot caught on PR #156.
    bool overrideLatestWatched = isWatching
        && item.LatestEpisodeDate is { } led
        && led.Date < today;

    if (isStatusActive(item.Status)
        && (!item.IsLatestEpisodeWatched || overrideLatestWatched)
        && HasAiredOrIsActivelyWatching(item, today))
    {
        availableNow.Add(item);
    }
}
```

Two bypasses, one principle: user said `Watching`, so we trust them over (a) "TMDb doesn't list this on a service you have" and (b) "you already watched the latest known episode."

## When to apply

- Any filter with the shape `if (externalDataAvailable && externalDataMatches) show()` — if the externalData is sometimes missing or stale, the filter's "false" branch may be the wrong default for items the user has actively committed to.
- Specifically: streaming-service gates, last-watched gates, region-availability gates, age-restriction gates inherited from upstream metadata.

## Anti-pattern

Layering ever-more-aggressive **refresh / backfill / cache-invalidation** machinery to keep external data fresh enough to satisfy the gate. Sometimes the data is *permanently* missing (see [[tmdb-providers-empty]] and [[verify-plan-premise-against-live-data]]) — no refresh cadence will fill it. The user-intent override is the deeper fix; refresh aggressiveness is a workaround.

## Counter-cases (when *not* to apply)

- For surfaces that explicitly answer "what can I watch on `<service>`" (a per-service chip filter), the gate is asking a narrower question and the override doesn't apply. Don't promote shows to a "demonstrably on Netflix" view just because the user is Watching them.
- For dismissed items (`Status == NotForMe`), the user committed in the *opposite* direction — surfacing them anywhere would be wrong.

The principle is "trust the user's signal where the signal *says yes*", not "ignore external data wholesale."
