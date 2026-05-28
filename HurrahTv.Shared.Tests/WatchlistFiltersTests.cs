using HurrahTv.Shared.Filters;
using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// covers the partition logic that produces the home page's two top rows. These tests
// pin the fall-through behaviour identified in issue #79 — a TV item that aired more
// than 7 days ago (with no upcoming episode within 14d) used to vanish from both rows
// even when it was streamable on a user's service. The new partition routes it to
// AvailableNow.
public class WatchlistFiltersTests
{
    private const int Netflix = 8;
    private const int Hulu = 15;
    private static readonly IReadOnlyList<int> UserHasNetflix = [Netflix];
    private static readonly DateTime Today = new(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);
    private static bool AllStatusesActive(QueueStatus _) => true;

    private static QueueItem TvItem(
        int id = 1,
        QueueStatus status = QueueStatus.WantToWatch,
        DateTime? latestEpisode = null,
        DateTime? nextEpisode = null,
        bool latestWatched = false,
        int providerId = Netflix) =>
        new()
        {
            Id = id,
            TmdbId = id,
            MediaType = MediaTypes.Tv,
            Status = status,
            LatestEpisodeDate = latestEpisode,
            NextEpisodeDate = nextEpisode,
            IsLatestEpisodeWatched = latestWatched,
            AvailableOnJson = $"[{providerId}]"
        };

    [Fact]
    public void AvailableNow_Includes_TvItem_That_Aired_Long_Ago_But_Is_Streamable()
    {
        QueueItem item = TvItem(latestEpisode: Today.AddDays(-30));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
        Assert.DoesNotContain(item, result.AvailableLater);
    }

    [Fact]
    public void AvailableNow_Excludes_TvItem_Not_Streamable_On_UserServices()
    {
        QueueItem item = TvItem(latestEpisode: Today.AddDays(-3), providerId: Hulu);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Includes_Watching_Item_With_No_ProviderData()
    {
        // pins #141 — a Watching show whose providers TMDb hasn't surfaced (empty
        // AvailableOnJson) used to be dropped from both Home rows by the streamability
        // gate. "Unknown providers" must be treated as "don't hide", matching the API.
        QueueItem item = TvItem(status: QueueStatus.Watching, latestEpisode: Today.AddDays(-1));
        item.AvailableOnJson = "[]";
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Excludes_TvItem_With_LatestEpisodeDate_In_Future()
    {
        // regression: the old predicate `DaysSinceLatestEpisode is <= 7` would silently
        // accept a negative value, classifying a future-dated episode as already-aired.
        QueueItem item = TvItem(latestEpisode: Today.AddDays(3));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Includes_Watching_Item_With_No_LatestEpisodeDate()
    {
        // a freshly-added Watching show before the TMDb episode-date backfill lands
        // should still surface in the active watchlist row.
        QueueItem item = TvItem(status: QueueStatus.Watching, latestEpisode: null);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Excludes_WantToWatch_With_No_LatestEpisodeDate()
    {
        QueueItem item = TvItem(status: QueueStatus.WantToWatch, latestEpisode: null);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Excludes_LatestEpisode_Already_Watched()
    {
        QueueItem item = TvItem(latestEpisode: Today.AddDays(-2), latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Respects_StatusChipFilter()
    {
        QueueItem item = TvItem(status: QueueStatus.Finished, latestEpisode: Today.AddDays(-2));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All,
            status => status != QueueStatus.Finished,
            UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Excludes_NotForMe()
    {
        QueueItem item = TvItem(status: QueueStatus.NotForMe, latestEpisode: Today.AddDays(-1));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableLater_Includes_NextEpisode_Within_Window()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(3));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableLater);
        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableLater_Excludes_NextEpisode_Today()
    {
        // strict future lower bound prevents a "0d" badge — today belongs to AvailableNow.
        QueueItem item = TvItem(nextEpisode: Today);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableLater);
    }

    [Fact]
    public void AvailableLater_Excludes_NextEpisode_In_The_Past()
    {
        // regression: the briefly-negative NextEpisodeDate case from #49/#70 must not
        // surface a negative day count anywhere in the UI.
        QueueItem item = TvItem(nextEpisode: Today.AddDays(-1));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableLater);
    }

    [Fact]
    public void AvailableLater_Excludes_NextEpisode_Beyond_Window()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(15));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableLater);
    }

    [Fact]
    public void AvailableLater_NotGatedBy_StatusChipFilter()
    {
        // a Finished show with a new-season premiere coming up must surface even when
        // the Finished chip is off — upcoming is forward-looking, not a watch-state filter.
        QueueItem item = TvItem(status: QueueStatus.Finished, nextEpisode: Today.AddDays(5));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All,
            status => status != QueueStatus.Finished,
            UserHasNetflix);

        Assert.Contains(item, result.AvailableLater);
        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableLater_Excludes_NotStreamable()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(3), providerId: Hulu);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableLater);
    }

    [Fact]
    public void MediaTypeFilter_TvOnly_Excludes_Movies()
    {
        QueueItem movie = new()
        {
            Id = 1,
            MediaType = MediaTypes.Movie,
            Status = QueueStatus.WantToWatch,
            AvailableOnJson = $"[{Netflix}]"
        };
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [movie], Today, MediaTypes.Tv, AllStatusesActive, UserHasNetflix);

        Assert.Empty(result.Movies);
    }

    [Fact]
    public void Movies_Includes_WantToWatch()
    {
        QueueItem movie = new()
        {
            Id = 1,
            MediaType = MediaTypes.Movie,
            Status = QueueStatus.WantToWatch,
            AvailableOnJson = $"[{Netflix}]"
        };
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [movie], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(movie, result.Movies);
        Assert.DoesNotContain(movie, result.AvailableNow);
        Assert.DoesNotContain(movie, result.AvailableLater);
    }

    [Fact]
    public void HasContent_Flags_Are_Independent_Of_MediaTypeFilter()
    {
        // section header must stay visible even when the active media-type filter
        // produces an empty row.
        QueueItem tv = TvItem(latestEpisode: Today.AddDays(-2));
        QueueItem movie = new()
        {
            Id = 2,
            MediaType = MediaTypes.Movie,
            Status = QueueStatus.WantToWatch,
            AvailableOnJson = $"[{Netflix}]"
        };
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [tv, movie], Today, MediaTypes.Tv, AllStatusesActive, UserHasNetflix);

        Assert.True(result.HasTvContent);
        Assert.True(result.HasMovieContent);
    }

    // pins #145 mode B (Kimmel-class) — a Watching daily show with the latest known
    // episode marked watched and more than 18h stale must STILL appear in Available
    // Now. The 18h heuristic catches TMDb's episode-data lag: by the time TMDb has
    // published today's Kimmel, more than 18h have passed since yesterday's marked-
    // watched episode. Without the override the show is hidden all day.
    [Fact]
    public void AvailableNow_Includes_Watching_Item_With_StaleWatchedLatestEpisode_PinsIssue145()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-1),
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
    }

    // bypass is bounded by the 18h staleness window — a Watching show whose latest
    // episode aired hours ago (fresh) and is marked watched still respects the gate,
    // because we have no reason to think TMDb's data is stale yet.
    [Fact]
    public void AvailableNow_Excludes_Watching_Item_With_FreshWatchedLatestEpisode()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddHours(-6),
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    // non-Watching statuses keep the full strict gate — only Watching opts into the
    // permissive override. A WantToWatch item with the latest episode marked watched
    // stays hidden regardless of how stale the date is.
    [Fact]
    public void AvailableNow_Excludes_NonWatching_Item_With_StaleWatchedLatestEpisode()
    {
        QueueItem item = TvItem(
            status: QueueStatus.WantToWatch,
            latestEpisode: Today.AddDays(-3),
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    // pins #145 mode B — a Watching show whose TMDb providers don't list any of the
    // user's services must STILL surface in Available Now. TMDb's data quirks for talk
    // shows (Kimmel on Hulu) and gray-zone content shouldn't hide what the user committed to.
    [Fact]
    public void AvailableNow_Includes_Watching_Item_Not_Streamable_On_UserServices_PinsIssue145()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-1),
            providerId: Hulu); // user only has Netflix
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
    }

    // Available Later is a stronger claim — "available on a user service soon" — so it
    // retains the strict streamability gate even for Watching items. Mirrors the plan's
    // decision #4: the override is Available-Now-scoped, not blanket.
    [Fact]
    public void AvailableLater_Excludes_Watching_Item_With_NonStreamable_UpcomingEpisode()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            nextEpisode: Today.AddDays(3),
            providerId: Hulu); // user only has Netflix
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableLater);
    }

    // pin the exact 18h fence with both sides — `(todayUtc - led).TotalHours > 18`.
    // 17h must NOT trigger the bypass; 19h MUST. Catches a regression that flips the
    // operator (>= vs >) or shifts the constant (12, 24, etc.) without updating tests.
    [Fact]
    public void AvailableNow_Excludes_Watching_Item_Just_Below_18h_Boundary()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddHours(-17),
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    [Fact]
    public void AvailableNow_Includes_Watching_Item_Just_Above_18h_Boundary()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddHours(-19),
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
    }
}
