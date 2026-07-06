using HurrahTv.Shared.Curation;

namespace HurrahTv.Shared.Tests;

// pins #229 — the persisted daily hero is served as a keyed read only while it's "fresh".
// Freshness = computed for today AND same watchlist hash; anything else forces a recompute.
public class DailyHeroFreshnessTests
{
    private static readonly DateOnly Today = new(2026, 7, 6);

    [Fact]
    public void DailyHero_IsFresh_Same_Day_Same_Hash() => Assert.True(DailyHeroFreshness.IsFresh(forDate: Today, storedHash: "abc", currentHash: "abc", today: Today));

    [Fact]
    public void DailyHero_IsStale_At_Utc_Day_Boundary()
    {
        // computed yesterday → stale today, even with a matching hash (daily rotation must advance)
        DateOnly yesterday = Today.AddDays(-1);
        Assert.False(DailyHeroFreshness.IsFresh(forDate: yesterday, storedHash: "abc", currentHash: "abc", today: Today));
    }

    [Fact]
    public void DailyHero_IsStale_On_Watchlist_Hash_Change() =>
        // same day, but the watchlist changed → the reservoir the pick came from is invalid
        Assert.False(DailyHeroFreshness.IsFresh(forDate: Today, storedHash: "abc", currentHash: "xyz", today: Today));

    [Fact]
    public void DailyHero_IsStale_When_Both_Day_And_Hash_Differ() => Assert.False(DailyHeroFreshness.IsFresh(forDate: Today.AddDays(-3), storedHash: "abc", currentHash: "xyz", today: Today));
}
