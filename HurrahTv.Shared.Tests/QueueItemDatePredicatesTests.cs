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
}
