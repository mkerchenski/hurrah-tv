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
            if (isWatching || isStreamable)
            {
                // for Watching, bypass IsLatestEpisodeWatched when LatestEpisodeDate is more
                // than 18h stale — TMDb's episode-data lag hides daily talk shows the day after
                // the user marks the previous episode watched (#145 mode B). The 12h
                // LastEpisodeCheckAt refresh on /api/queue limits how often this safety net fires.
                bool overrideLatestWatched = isWatching
                    && item.LatestEpisodeDate is { } led
                    && (todayUtc - led).TotalHours > 18;

                if (isStatusActive(item.Status)
                    && (!item.IsLatestEpisodeWatched || overrideLatestWatched)
                    && HasAiredOrIsActivelyWatching(item, today))
                {
                    availableNow.Add(item);
                }
            }

            // Available Later still requires streamability — "available on a user service soon"
            // is a stronger claim than Available Now's "you've committed to this".
            if (isStreamable && item.NextEpisodeDate is { } next && next.Date > today && next.Date <= windowEnd)
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
