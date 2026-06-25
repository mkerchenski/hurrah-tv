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

    // pins #170 — #145's Watching override keyed on "LatestEpisodeDate.Date < today",
    // which is true for nearly every show, so marking the latest watched never removed a
    // Watching show from Available Now. The earlier already-watched test missed this
    // because TvItem defaults to WantToWatch (the override only fires for Watching).
    [Fact]
    public void AvailableNow_Excludes_Watching_LatestEpisode_Watched_When_No_NewerEpisode()
    {
        // caught up: latest aired 2 days ago and is watched; next episode is still upcoming.
        QueueItem item = TvItem(status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-2), nextEpisode: Today.AddDays(5), latestWatched: true);
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

    // pins #196 — "available today" for a streaming release usually means it drops sometime today
    // and isn't watchable yet, so a latest episode dated today belongs in Upcoming (badged "today"),
    // not Available Now. Membership is preserved: the item still shows, it just moves rows.
    [Fact]
    public void AvailableNow_Excludes_TvItem_Dropping_Today_RoutesToLater()
    {
        QueueItem item = TvItem(latestEpisode: Today);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
        Assert.Contains(item, result.AvailableLater);
    }

    // boundary partner to the drops-today test: yesterday definitely aired → stays in Available Now.
    [Fact]
    public void AvailableNow_Includes_TvItem_That_Aired_Yesterday()
    {
        QueueItem item = TvItem(latestEpisode: Today.AddDays(-1));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
        Assert.DoesNotContain(item, result.AvailableLater);
    }

    // the Watching bypass that keeps no-provider shows (Kimmel-class) visible must survive the
    // now→later relocation — a Watching show dropping today must not vanish from both rows.
    [Fact]
    public void AvailableLater_Includes_Watching_TodayDropper_With_No_ProviderData()
    {
        QueueItem item = TvItem(status: QueueStatus.Watching, latestEpisode: Today);
        item.AvailableOnJson = "[]";
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
        Assert.Contains(item, result.AvailableLater);
    }

    // a today-dropper that ALSO has a next episode within the window must appear in Upcoming
    // exactly once — the addedToLater guard prevents the double-add.
    [Fact]
    public void AvailableLater_TodayDropper_With_UpcomingEpisode_AppearsOnce()
    {
        QueueItem item = TvItem(latestEpisode: Today, nextEpisode: Today.AddDays(7));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
        Assert.Single(result.AvailableLater);
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
        // beyond the 30-day window (#214 widened from 14) → excluded.
        QueueItem item = TvItem(nextEpisode: Today.AddDays(31));
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

    // #214: Available Later dropped the streamability gate — an upcoming episode on a service the
    // user does NOT subscribe to (Hulu; user has only Netflix) must now surface. This test asserted
    // the inverse before #214.
    [Fact]
    public void AvailableLater_Includes_NonStreamable_UpcomingEpisode_PinsIssue214()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(3), providerId: Hulu);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableLater);
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

    // pins #145 mode B (Kimmel-class), corrected by #170/#172 — a caught-up Watching show
    // resurfaces ONLY when a newer episode's air day has *fully passed* (NextEpisodeDate.Date
    // < today), meaning it definitely aired and the 12h refresh simply hasn't advanced
    // LatestEpisode* yet. Strictly-past, not <= today, so a show airing later *today* doesn't
    // resurface before its episode is watchable (#172).
    [Fact]
    public void AvailableNow_Includes_Watching_Watched_WhenNextEpisodeDayHasPassed_PinsIssue145()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-2),
            nextEpisode: Today.AddDays(-1),  // its air day fully elapsed → genuinely aired, refresh lagging
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableNow);
    }

    // pins #172 — a daily show whose next episode airs *today* (but hasn't aired yet) must NOT
    // resurface after the user watches the prior episode. air_date is date-only, so
    // NextEpisodeDate == today can't be assumed watchable; <= today wrongly kept caught-up
    // daily shows in Available Now all day.
    [Fact]
    public void AvailableNow_Excludes_Watching_Watched_WhenNextEpisodeAirsLaterToday()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-1),
            nextEpisode: Today,
            latestWatched: true);
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableNow);
    }

    // #170: the override fires only on NextEpisodeDate having aired, so a caught-up Watching
    // show with NO scheduled next episode (hiatus, or a season finale TMDb hasn't followed
    // with a next season) leaves Available Now once the latest is marked watched — there's
    // genuinely nothing new to surface. Pins the null-NextEpisodeDate branch of the override.
    [Fact]
    public void AvailableNow_Excludes_Watching_Watched_WithNoUpcomingEpisode()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-3),
            nextEpisode: null,
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

    // pins the empty-userServices boundary: IsStreamableOn returns false when the
    // user has no configured services (see IsStreamableOn_NoUserServices_ReturnsFalse),
    // but the Watching override should still surface the item. The user-intent rule
    // is unconditional — a future refactor that re-checks streamability before the
    // Watching branch would silently regress this. (Copilot flagged the gap on PR #156.)
    [Fact]
    public void AvailableNow_Includes_Watching_Item_When_UserServices_Are_Empty()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            latestEpisode: Today.AddDays(-1));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, userServices: []);

        Assert.Contains(item, result.AvailableNow);
    }

    // #214 reverses the earlier "Later keeps the streamability gate" decision (was Available-Now-
    // scoped only): Available Later is now the forward-looking radar for ANY show, so a Watching item
    // with an upcoming episode on a non-subscribed service (Hulu; user has only Netflix) surfaces too.
    [Fact]
    public void AvailableLater_Includes_Watching_NonStreamable_UpcomingEpisode_PinsIssue214()
    {
        QueueItem item = TvItem(
            status: QueueStatus.Watching,
            nextEpisode: Today.AddDays(3),
            providerId: Hulu); // user only has Netflix
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableLater);
    }

    // #214: a user with ZERO configured services still sees Available Later populated. The old gate
    // made IsStreamableOn return false for everything, emptying the row for service-less users.
    [Fact]
    public void AvailableLater_Includes_Upcoming_When_UserServices_Are_Empty_PinsIssue214()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(3));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, userServices: []);

        Assert.Contains(item, result.AvailableLater);
    }

    // #214 widened the window to 30 days — an episode on day 30 (inclusive upper bound) appears...
    [Fact]
    public void AvailableLater_Includes_NextEpisode_At_30Day_Boundary_PinsIssue214()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(30));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableLater);
    }

    // ...and an episode 20 days out — beyond the OLD 14-day window — now surfaces where it didn't.
    [Fact]
    public void AvailableLater_Includes_NextEpisode_Beyond_Old_14Day_Window_PinsIssue214()
    {
        QueueItem item = TvItem(nextEpisode: Today.AddDays(20));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.Contains(item, result.AvailableLater);
    }

    // #214: dropping the streamability gate must NOT leak dismissed (NotForMe) shows into Later.
    [Fact]
    public void AvailableLater_Excludes_NotForMe_With_UpcomingEpisode_PinsIssue214()
    {
        QueueItem item = TvItem(status: QueueStatus.NotForMe, nextEpisode: Today.AddDays(3));
        WatchlistFilters.Partition result = WatchlistFilters.Apply(
            [item], Today, MediaTypes.All, AllStatusesActive, UserHasNetflix);

        Assert.DoesNotContain(item, result.AvailableLater);
    }

}
