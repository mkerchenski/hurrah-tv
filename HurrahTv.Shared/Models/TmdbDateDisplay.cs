namespace HurrahTv.Shared.Models;

// Display + aired/upcoming classification for TMDb date-only fields (air_date / first_air_date).
// All comparisons are typed against todayUtc.Date — never a signed day-diff integer, whose sign
// the `is <= N` / `(int)TotalDays` patterns hide (see Learnings/date-predicates-prefer-typed-comparisons.md).
// todayUtc is injected (not DateTime.UtcNow inline) so the date fences are testable without
// midnight-UTC drift, mirroring HurrahTv.Shared/Filters/WatchlistFilters.cs.
//
// Hurrah.tv standardizes client TMDb date display on a UTC "today" for cross-surface consistency
// with the Home page's server-side classification (#192). Callers pass DateTime.UtcNow.Date.
public static class TmdbDateDisplay
{
    // aired within the last `withinDays`, and not in the future. The `dayDelta <= 0` arm is what
    // keeps an unreleased (future) date from reading as "recent" — the signed-int bug #192/2a.
    public static bool IsRecent(string? raw, DateTime todayUtc, int withinDays = 30)
    {
        if (!TmdbDate.TryParse(raw, out DateTime date)) return false;
        int dayDelta = (date.Date - todayUtc.Date).Days;
        return dayDelta <= 0 && dayDelta >= -withinDays;
    }

    // relative phrasing ("today" / "tomorrow" / "in 3 days" / "yesterday" / "5 days ago" /
    // "2 weeks ago"), falling back to an absolute date outside ±7d (future) / -30d (past).
    // dayDelta is the exact calendar-day difference of two midnights — sign-correct, so a
    // near-future date can't truncate to 0 and mislabel as "today" (the bug #192/2b).
    public static string FormatRelative(string? raw, DateTime todayUtc)
    {
        if (!TmdbDate.TryParse(raw, out DateTime date)) return "";
        int dayDelta = (date.Date - todayUtc.Date).Days;
        return dayDelta switch
        {
            0 => "today",
            1 => "tomorrow",
            > 1 and <= 7 => $"in {dayDelta} days",
            -1 => "yesterday",
            < -1 and >= -7 => $"{-dayDelta} days ago",
            < -7 and >= -30 => $"{-dayDelta / 7} weeks ago",
            _ => date.ToString("MMM d, yyyy"),
        };
    }

    // absolute "MMM d, yyyy", or "" when missing/unparseable (TryParse rejects "0000-00-00").
    public static string FormatAbsolute(string? raw) =>
        TmdbDate.TryParse(raw, out DateTime date) ? date.ToString("MMM d, yyyy") : "";

    // single pass over a season's episodes (parses each AirDate once): everything aired on or
    // before todayUtc goes to Aired; the earliest still-upcoming episode (lowest episode number)
    // is NextUp. Episodes with a missing/unparseable AirDate are unknown — dropped from both.
    public static (IReadOnlyList<EpisodeInfo> Aired, EpisodeInfo? NextUp) SplitAiredUpcoming(
        IReadOnlyList<EpisodeInfo> episodes, DateTime todayUtc)
    {
        List<EpisodeInfo> aired = [];
        EpisodeInfo? nextUp = null;
        foreach (EpisodeInfo ep in episodes)
        {
            if (!TmdbDate.TryParse(ep.AirDate, out DateTime date)) continue;
            if (date.Date <= todayUtc.Date)
                aired.Add(ep);
            else if (nextUp is null || ep.EpisodeNumber < nextUp.EpisodeNumber)
                nextUp = ep;
        }
        return (aired, nextUp);
    }
}
