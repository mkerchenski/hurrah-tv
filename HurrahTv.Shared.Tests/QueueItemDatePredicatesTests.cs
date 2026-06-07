using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// LatestEpisodeDate / NextEpisodeDate come from Postgres TIMESTAMPTZ (Kind=Utc);
// these predicates compare against DateTime.UtcNow. Tests construct UTC DateTimes
// to match the production data shape.
public class QueueItemDatePredicatesTests
{
    [Fact]
    public void HasUpcomingEpisode_IsFalse_WhenNextEpisodeDateIsInThePast()
    {
        QueueItem item = new() { NextEpisodeDate = DateTime.UtcNow.AddDays(-1) };
        Assert.False(item.HasUpcomingEpisode);
    }

    [Fact]
    public void HasUpcomingEpisode_IsTrue_WhenNextEpisodeDateIsWithinWindow()
    {
        QueueItem item = new() { NextEpisodeDate = DateTime.UtcNow.AddDays(3) };
        Assert.True(item.HasUpcomingEpisode);
    }

    [Fact]
    public void HasUpcomingEpisode_IsFalse_WhenNextEpisodeDateIsBeyondWindow()
    {
        QueueItem item = new() { NextEpisodeDate = DateTime.UtcNow.AddDays(8) };
        Assert.False(item.HasUpcomingEpisode);
    }

    [Fact]
    public void HasUpcomingEpisode_IsFalse_WhenNextEpisodeDateIsNull()
    {
        QueueItem item = new() { NextEpisodeDate = null };
        Assert.False(item.HasUpcomingEpisode);
    }

    [Fact]
    public void HasNewEpisode_IsTrue_WhenLatestEpisodeDateIsWithin7Days()
    {
        QueueItem item = new() { LatestEpisodeDate = DateTime.UtcNow.AddDays(-3) };
        Assert.True(item.HasNewEpisode);
    }

    [Fact]
    public void HasNewEpisode_IsFalse_WhenLatestEpisodeDateIsOlderThan7Days()
    {
        QueueItem item = new() { LatestEpisodeDate = DateTime.UtcNow.AddDays(-8) };
        Assert.False(item.HasNewEpisode);
    }

    // pins #86 — wrong-sign LatestEpisodeDate (TMDb backfill quirk) must not pass the
    // window. Home hero billboard (Home.razor:754) and Queue card badge (Queue.razor:137)
    // both gate UI on this predicate; future-stamped items would feature unaired episodes.
    [Fact]
    public void HasNewEpisode_IsFalse_WhenLatestEpisodeDateIsInTheFuture()
    {
        QueueItem item = new() { LatestEpisodeDate = DateTime.UtcNow.AddDays(5) };
        Assert.False(item.HasNewEpisode);
    }

    [Fact]
    public void HasEpisodeThisMonth_IsTrue_WhenLatestEpisodeDateIsWithin30Days()
    {
        QueueItem item = new() { LatestEpisodeDate = DateTime.UtcNow.AddDays(-20) };
        Assert.True(item.HasEpisodeThisMonth);
    }

    [Fact]
    public void HasEpisodeThisMonth_IsFalse_WhenLatestEpisodeDateIsOlderThan30Days()
    {
        QueueItem item = new() { LatestEpisodeDate = DateTime.UtcNow.AddDays(-31) };
        Assert.False(item.HasEpisodeThisMonth);
    }

    [Fact]
    public void HasEpisodeThisMonth_IsFalse_WhenLatestEpisodeDateIsNull()
    {
        QueueItem item = new() { LatestEpisodeDate = null };
        Assert.False(item.HasEpisodeThisMonth);
    }

    // pins #86 — Home.razor:683 uses HasEpisodeThisMonth as a sort key in the default
    // watchlist sort; a future-stamped item without an upper bound would otherwise
    // sort to the top alongside legitimate recent-episode items.
    [Fact]
    public void HasEpisodeThisMonth_IsFalse_WhenLatestEpisodeDateIsInTheFuture()
    {
        QueueItem item = new() { LatestEpisodeDate = DateTime.UtcNow.AddDays(10) };
        Assert.False(item.HasEpisodeThisMonth);
    }

    // --- CanMarkLatestEpisodeWatched: the QuickActions "Episode Watched" gate (#168) ---
    // builds a Watching TV item with populated, unwatched, already-aired latest-episode
    // metadata — the baseline that should pass — so each test flips one condition.
    private static QueueItem MarkableTvItem(DateTime todayUtc) => new()
    {
        MediaType = MediaTypes.Tv,
        Status = QueueStatus.Watching,
        LatestEpisodeSeason = 1,
        LatestEpisodeNumber = 5,
        IsLatestEpisodeWatched = false,
        LatestEpisodeDate = todayUtc.AddDays(-3),
    };

    // pins #168 — the original bug: a 7-day recency fence hid the action on Available Now
    // shows whose latest episode aired more than a week ago. No window now.
    [Fact]
    public void CanMarkLatestEpisodeWatched_IsTrue_WhenLatestEpisodeAiredMoreThan7DaysAgo()
    {
        DateTime today = DateTime.UtcNow;
        QueueItem item = MarkableTvItem(today);
        item.LatestEpisodeDate = today.AddDays(-30);
        Assert.True(item.CanMarkLatestEpisodeWatched(today));
    }

    // today-inclusive: air_date is date-only and we'd rather over-show a live-watched episode.
    [Fact]
    public void CanMarkLatestEpisodeWatched_IsTrue_WhenLatestEpisodeAiredToday()
    {
        DateTime today = new(2026, 6, 7, 14, 0, 0, DateTimeKind.Utc);
        QueueItem item = MarkableTvItem(today);
        item.LatestEpisodeDate = new DateTime(2026, 6, 7, 2, 0, 0, DateTimeKind.Utc); // earlier same UTC day
        Assert.True(item.CanMarkLatestEpisodeWatched(today));
    }

    // typed date comparison must reject a future-stamped LatestEpisodeDate (TMDb backfill
    // quirk) — a signed day-diff <= window would have let an unaired episode through (#49/#70).
    [Fact]
    public void CanMarkLatestEpisodeWatched_IsFalse_WhenLatestEpisodeIsInTheFuture()
    {
        DateTime today = DateTime.UtcNow;
        QueueItem item = MarkableTvItem(today);
        item.LatestEpisodeDate = today.AddDays(2);
        Assert.False(item.CanMarkLatestEpisodeWatched(today));
    }

    [Fact]
    public void CanMarkLatestEpisodeWatched_IsFalse_WhenAlreadyWatched()
    {
        DateTime today = DateTime.UtcNow;
        QueueItem item = MarkableTvItem(today);
        item.IsLatestEpisodeWatched = true;
        Assert.False(item.CanMarkLatestEpisodeWatched(today));
    }

    [Theory]
    [InlineData(QueueStatus.WantToWatch)]
    [InlineData(QueueStatus.Finished)]
    [InlineData(QueueStatus.NotForMe)]
    public void CanMarkLatestEpisodeWatched_IsFalse_WhenNotWatching(QueueStatus status)
    {
        DateTime today = DateTime.UtcNow;
        QueueItem item = MarkableTvItem(today);
        item.Status = status;
        Assert.False(item.CanMarkLatestEpisodeWatched(today));
    }

    [Fact]
    public void CanMarkLatestEpisodeWatched_IsFalse_ForMovies()
    {
        DateTime today = DateTime.UtcNow;
        QueueItem item = MarkableTvItem(today);
        item.MediaType = MediaTypes.Movie;
        Assert.False(item.CanMarkLatestEpisodeWatched(today));
    }

    // episode metadata not yet refreshed from TMDb — gate is false (the QuickActions
    // "Episode info updating…" hint covers this case instead of the action).
    [Fact]
    public void CanMarkLatestEpisodeWatched_IsFalse_WhenEpisodeMetadataNotPopulated()
    {
        DateTime today = DateTime.UtcNow;
        QueueItem item = MarkableTvItem(today);
        item.LatestEpisodeSeason = null;
        item.LatestEpisodeNumber = null;
        Assert.False(item.CanMarkLatestEpisodeWatched(today));
    }

    [Fact]
    public void CanMarkLatestEpisodeWatched_IsTrue_ForBaselineWatchingAiredUnwatchedTvItem()
    {
        DateTime today = DateTime.UtcNow;
        Assert.True(MarkableTvItem(today).CanMarkLatestEpisodeWatched(today));
    }
}
