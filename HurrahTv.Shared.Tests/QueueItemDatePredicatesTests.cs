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
}
