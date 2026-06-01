using HurrahTv.Shared.Curation;

namespace HurrahTv.Shared.Tests;

// pins #135 — the daily Home-hero rotation. The worst bugs here are off-by-one cooldown
// fences and within-day instability (the hero reshuffling on a page refresh), so these
// drive the exact day boundaries with an injected `Today`.
//
// Impressions are keyed by (TmdbId, MediaType) — TMDb ids are namespaced per media type,
// so a movie and a TV show can share a numeric id (#146).
public class HeroSelectorTests
{
    private static readonly DateTime Today = new(2026, 5, 27, 0, 0, 0, DateTimeKind.Utc);

    private static Dictionary<(int TmdbId, string MediaType), DateTime> NoImpressions() => [];

    [Fact]
    public void EmptyReservoir_ReturnsNull() =>
        Assert.Null(HeroSelector.Select([], NoImpressions(), Today));

    [Fact]
    public void NoImpressions_PicksHighestScore()
    {
        List<HeroCandidate> reservoir = [new(5, "tv", 60), new(9, "movie", 95), new(2, "tv", 80)];
        Assert.Equal(9, HeroSelector.Select(reservoir, NoImpressions(), Today)!.TmdbId);
    }

    [Fact]
    public void EqualScores_TieBreakByLowestTmdbId()
    {
        List<HeroCandidate> reservoir = [new(30, "tv", 88), new(12, "tv", 88)];
        Assert.Equal(12, HeroSelector.Select(reservoir, NoImpressions(), Today)!.TmdbId);
    }

    [Fact]
    public void RecentlyShown_IsExcluded_DuringCooldown()
    {
        // #1 is the highest score but was featured 3 days ago — well inside the window.
        List<HeroCandidate> reservoir = [new(1, "tv", 90), new(2, "tv", 80)];
        Dictionary<(int, string), DateTime> shown = new() { [(1, "tv")] = Today.AddDays(-3) };
        Assert.Equal(2, HeroSelector.Select(reservoir, shown, Today)!.TmdbId);
    }

    [Fact]
    public void CooldownFence_ShownExactlyCooldownDaysAgo_StillExcluded()
    {
        List<HeroCandidate> reservoir = [new(1, "tv", 90), new(2, "tv", 50)];

        // exactly 14 days ago → (today - day).Days == 14, NOT > 14 → still excluded.
        Dictionary<(int, string), DateTime> shown = new() { [(1, "tv")] = Today.AddDays(-14) };
        Assert.Equal(2, HeroSelector.Select(reservoir, shown, Today)!.TmdbId);

        // 15 days ago → cooled down, and it outscores #2.
        shown[(1, "tv")] = Today.AddDays(-15);
        Assert.Equal(1, HeroSelector.Select(reservoir, shown, Today)!.TmdbId);
    }

    [Fact]
    public void ShownToday_StaysEligible_SoPickIsStableWithinTheDay()
    {
        List<HeroCandidate> reservoir = [new(1, "tv", 90), new(2, "tv", 80)];
        Dictionary<(int, string), DateTime> shown = [];

        HeroCandidate? first = HeroSelector.Select(reservoir, shown, Today);
        shown[(first!.TmdbId, first.MediaType)] = Today; // endpoint records today's impression

        // a later visit the same day (different clock time, same calendar day) must not reshuffle.
        HeroCandidate? second = HeroSelector.Select(reservoir, shown, Today.AddHours(8));
        Assert.Equal(first.TmdbId, second!.TmdbId);
    }

    [Fact]
    public void DailyRotation_TopPickYesterday_AdvancesToNextBestToday()
    {
        List<HeroCandidate> reservoir = [new(1, "tv", 90), new(2, "tv", 80), new(3, "movie", 70)];
        Dictionary<(int, string), DateTime> shown = new() { [(1, "tv")] = Today.AddDays(-1) }; // featured yesterday
        Assert.Equal(2, HeroSelector.Select(reservoir, shown, Today)!.TmdbId);
    }

    [Fact]
    public void NoRepeat_WithinCooldown_OverADailyWalk()
    {
        // reservoir deeper than the cooldown window so rotation never needs the fallback
        List<HeroCandidate> reservoir = [.. Enumerable.Range(1, 20).Select(i => new HeroCandidate(i, "tv", 100 - i))];
        Dictionary<(int, string), DateTime> shown = [];
        List<int> picks = [];

        DateTime day = Today;
        for (int d = 0; d < 14; d++)
        {
            HeroCandidate? pick = HeroSelector.Select(reservoir, shown, day);
            Assert.NotNull(pick);
            picks.Add(pick!.TmdbId);
            shown[(pick.TmdbId, pick.MediaType)] = day; // record what was featured that day
            day = day.AddDays(1);
        }

        Assert.Equal(14, picks.Distinct().Count()); // no title repeats inside the window
    }

    [Fact]
    public void Refresh_ExcludesTodaysPick_AndAdvancesToNext()
    {
        List<HeroCandidate> reservoir = [new(1, "tv", 90), new(2, "tv", 80)];
        Dictionary<(int, string), DateTime> shown = new() { [(1, "tv")] = Today }; // already featured today

        // normal path keeps today's pick (stability); refresh advances past it.
        Assert.Equal(1, HeroSelector.Select(reservoir, shown, Today)!.TmdbId);
        Assert.Equal(2, HeroSelector.Select(reservoir, shown, Today, keepTodaysPickEligible: false)!.TmdbId);
    }

    [Fact]
    public void ThinReservoir_AllInCooldown_FallsBackToLeastRecentlyShown()
    {
        List<HeroCandidate> reservoir = [new(1, "tv", 90), new(2, "tv", 80)];
        Dictionary<(int, string), DateTime> shown = new()
        {
            [(1, "tv")] = Today.AddDays(-2), // older
            [(2, "tv")] = Today.AddDays(-1), // more recent
        };
        // both inside the cooldown and neither shown today → nothing eligible. Degrade to
        // the least-recently-shown title rather than going blank.
        Assert.Equal(1, HeroSelector.Select(reservoir, shown, Today)!.TmdbId);
    }

    // pins #146 — a movie and a TV show sharing a numeric TMDb id must not share a cooldown.
    // The tv title was featured today; the movie with the same id has never been shown and
    // must stay eligible (a TmdbId-only key would wrongly suppress it).
    [Fact]
    public void SameTmdbId_DifferentMediaType_HaveIndependentCooldowns()
    {
        List<HeroCandidate> reservoir = [new(1399, "tv", 90), new(1399, "movie", 80)];
        Dictionary<(int, string), DateTime> shown = new() { [(1399, "tv")] = Today.AddDays(-1) };

        // the tv title is inside its cooldown, but the movie (same id, never shown) is eligible.
        HeroCandidate? pick = HeroSelector.Select(reservoir, shown, Today);
        Assert.Equal(1399, pick!.TmdbId);
        Assert.Equal("movie", pick.MediaType);
    }
}
