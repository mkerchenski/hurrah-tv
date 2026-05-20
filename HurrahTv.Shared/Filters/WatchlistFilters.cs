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

            // streamability parses AvailableOnJson — keep it inside the single pass so each
            // item is touched at most once regardless of how many target rows it qualifies for.
            if (!item.IsStreamableOn(userServices)) continue;

            // chip filter gates AvailableNow (the active watchlist) but not AvailableLater —
            // a new-season premiere on a Finished show should still surface when the Finished
            // chip is off, since "later" is forward-looking and independent of watch state.
            if (isStatusActive(item.Status)
                && !item.IsLatestEpisodeWatched
                && HasAiredOrIsActivelyWatching(item, today))
            {
                availableNow.Add(item);
            }

            if (item.NextEpisodeDate is { } next && next.Date > today && next.Date <= windowEnd)
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
