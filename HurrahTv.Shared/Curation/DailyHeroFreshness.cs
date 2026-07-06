namespace HurrahTv.Shared.Curation;

// Whether a persisted daily hero (CurationDailyHero, #229) can be served as-is or must be
// recomputed. The hero is a within-day-stable pick derived from a watchlist-hashed reservoir,
// so the stored row is fresh only when it was computed for *today* AND the watchlist hasn't
// changed since (a changed watchlist invalidates the reservoir the pick came from).
//
// Date is injected (today) rather than read from DateTime.UtcNow so the UTC day boundary is
// testable, and compared as DateOnly so there's no time-of-day / DateTimeKind ambiguity.
public static class DailyHeroFreshness
{
    public static bool IsFresh(DateOnly forDate, string storedHash, string currentHash, DateOnly today) =>
        forDate == today && storedHash == currentHash;
}
