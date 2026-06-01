namespace HurrahTv.Shared.Curation;

// A candidate the Home hero can feature, drawn from the AI-scored reservoir.
public record HeroCandidate(int TmdbId, string MediaType, int Score);

// Picks one confident title for the Home hero, rotating daily without repeating a title
// within a cooldown window. Pure + deterministic: given the same reservoir, impression
// history, and date it always returns the same pick — so the hero is stable within a
// calendar day and re-picks at the UTC midnight boundary. pins #135.
//
// Time is injected (todayUtc) rather than read from DateTime.UtcNow so the rotation and
// cooldown fences can be tested at exact day boundaries.
public static class HeroSelector
{
    public const int DefaultCooldownDays = 14;

    // Returns the title to feature, or null if the reservoir is empty.
    //
    // Best-eligible policy. A candidate is eligible when it was:
    //   - never shown, OR
    //   - last shown *today* — so recording today's pick doesn't bump it out of contention
    //     for the rest of today (this is what makes the daily pick stable within the day), OR
    //   - last shown more than cooldownDays ago.
    // Among eligible candidates the highest Score wins (tie-break: lowest TmdbId, for
    // determinism). When the cooldown leaves nothing eligible — a reservoir too thin to fill
    // the window — fall back to the least-recently-shown title so the hero always has
    // something rather than going blank.
    public static HeroCandidate? Select(
        IReadOnlyList<HeroCandidate> reservoir,
        IReadOnlyDictionary<(int TmdbId, string MediaType), DateTime> lastShownUtc,
        DateTime todayUtc,
        int cooldownDays = DefaultCooldownDays,
        bool keepTodaysPickEligible = true)
    {
        if (reservoir.Count == 0) return null;

        DateTime today = todayUtc.Date;

        bool IsEligible(HeroCandidate c)
        {
            // keyed by (TmdbId, MediaType): a movie and a TV show can share a numeric id, so a
            // TmdbId-only lookup would let one media type's cooldown suppress the other (#146).
            if (!lastShownUtc.TryGetValue((c.TmdbId, c.MediaType), out DateTime shown)) return true; // never featured
            DateTime shownDay = shown.Date;
            // shown today normally stays eligible (so a same-day re-fetch is stable), but a
            // manual refresh passes keepTodaysPickEligible=false to advance past today's pick.
            if (shownDay == today) return keepTodaysPickEligible;
            return (today - shownDay).Days > cooldownDays;     // out of the cooldown window
        }

        HeroCandidate? best = reservoir
            .Where(IsEligible)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.TmdbId)
            .FirstOrDefault();

        if (best is not null) return best;

        // every candidate is inside the cooldown — degrade gracefully to whichever was
        // featured longest ago (never-shown items are always eligible above, so they
        // can't reach this branch).
        return reservoir
            .OrderBy(c => lastShownUtc.TryGetValue((c.TmdbId, c.MediaType), out DateTime shown) ? shown : DateTime.MinValue)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.TmdbId)
            .First();
    }
}
