using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Filters;

// Predicates compare DateTime values directly rather than going through integer day-diffs,
// because a wrong-sign result (e.g. a future-stamped LatestEpisodeDate from a TMDb edge case)
// would silently pass `is <= 7` and classify an unaired episode as already-available.
public static class WatchlistFilters
{
    public const int AvailableLaterWindowDays = 14;

    public record Partition(
        List<QueueItem> AvailableNow,
        List<QueueItem> AvailableLater,
        List<QueueItem> Movies,
        bool HasTvContent,
        bool HasMovieContent);

    public static Partition Apply(
        IReadOnlyList<QueueItem> allItems,
        DateTime todayUtc,
        string mediaType,
        Func<QueueStatus, bool> isStatusActive,
        IReadOnlyList<int> userServices)
    {
        DateTime today = todayUtc.Date;
        DateTime windowEnd = today.AddDays(AvailableLaterWindowDays);

        List<QueueItem> availableNow = [];
        List<QueueItem> availableLater = [];
        List<QueueItem> movies = [];
        bool hasTvContent = false;
        bool hasMovieContent = false;

        foreach (QueueItem item in allItems)
        {
            bool isTv = item.MediaType == MediaTypes.Tv;
            bool isMovie = item.MediaType == MediaTypes.Movie;
            bool dismissed = item.Status == QueueStatus.NotForMe;
            bool mediaMatch = mediaType == MediaTypes.All || item.MediaType == mediaType;

            // pre-filter existence flags ignore the mediaType filter so the section header
            // stays visible while the user flips chips.
            if (isTv && !dismissed) hasTvContent = true;
            if (isMovie && item.Status == QueueStatus.WantToWatch) hasMovieContent = true;

            if (dismissed || !mediaMatch) continue;

            if (isMovie)
            {
                if (item.Status == QueueStatus.WantToWatch) movies.Add(item);
                continue;
            }

            if (!isTv) continue;

            // streamability parses AvailableOnJson — compute once and reuse for both rows below.
            bool isStreamable = item.IsStreamableOn(userServices);
            bool isWatching = item.Status == QueueStatus.Watching;

            // Available Now: Watching status bypasses the streamability gate (the user committed;
            // we trust them even when TMDb has no recognized providers — pins #145 mode B for
            // shows like Kimmel where TMDb doesn't list Hulu). Non-Watching items keep the strict
            // gate so the row stays "things you can actually watch".
            // chip filter gates AvailableNow (the active watchlist) but not AvailableLater —
            // a new-season premiere on a Finished show should still surface when the Finished
            // chip is off, since "later" is forward-looking and independent of watch state.
            bool addedToLater = false;

            if (isWatching || isStreamable)
            {
                // for Watching, resurface a caught-up show (latest episode marked watched) ONLY
                // when a genuinely newer episode has aired that the 12h /api/queue refresh hasn't
                // folded into LatestEpisode* yet — i.e. NextEpisodeDate's air day has fully
                // PASSED (< today). air_date is date-only, so a NextEpisodeDate of *today* hasn't
                // necessarily aired (a daily show airing tonight) — using <= today resurfaced a
                // caught-up daily show on its air day before the episode was watchable (#172).
                // Strictly-past means a full calendar day has elapsed, so it definitely aired and
                // our data is merely stale. The original #145 override keyed on LatestEpisodeDate,
                // which matched nearly every show and never removed watched shows (#170).
                bool overrideLatestWatched = isWatching
                    && item.NextEpisodeDate is { } nextAired
                    && nextAired.Date < today;

                if (isStatusActive(item.Status)
                    && (!item.IsLatestEpisodeWatched || overrideLatestWatched)
                    && HasAiredOrIsActivelyWatching(item, today))
                {
                    // A latest episode dated *today* is "available today" — for a streaming release
                    // that usually means it drops at some point during the day and isn't watchable
                    // yet, so it belongs in the forward-looking Upcoming row (badged "today") rather
                    // than Available Now (#196). We keep the full Available Now gate above (incl. the
                    // Watching bypass) and only relocate WHICH row it renders in — so no item that
                    // showed before disappears, it just moves from "now" to "later".
                    if (item.LatestEpisodeDate is { } latest && latest.Date == today)
                    {
                        availableLater.Add(item);
                        addedToLater = true;
                    }
                    else
                    {
                        availableNow.Add(item);
                    }
                }
            }

            // Available Later also surfaces a strictly-future next episode within the window. It
            // requires streamability — "available on a user service soon" is a stronger claim than
            // Available Now's "you've committed to this". The addedToLater guard prevents adding an
            // item twice when it both drops today (routed above) and has a next episode in-window.
            if (!addedToLater && isStreamable
                && item.NextEpisodeDate is { } next && next.Date > today && next.Date <= windowEnd)
            {
                availableLater.Add(item);
            }
        }

        return new Partition(availableNow, availableLater, movies, hasTvContent, hasMovieContent);
    }

    private static bool HasAiredOrIsActivelyWatching(QueueItem item, DateTime today) =>
        item.LatestEpisodeDate is { } latest
            ? latest.Date <= today
            : item.Status == QueueStatus.Watching;
}
