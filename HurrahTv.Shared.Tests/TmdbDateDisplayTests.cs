using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// TmdbDateDisplay owns aired/upcoming classification + relative phrasing for TMDb date-only
// strings, using typed comparisons against an injected todayUtc. These pin the two reachable
// signed-day-diff bugs from #192 (2a: future reads as recent; 2b: near-future labels as "today")
// and the aired/upcoming split. todayUtc is fixed so the date fences don't drift across midnight.
public class TmdbDateDisplayTests
{
    private static readonly DateTime Today = new(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);

    private static string Raw(int dayOffset) => $"{Today.AddDays(dayOffset):yyyy-MM-dd}";

    private static EpisodeInfo Ep(int number, string? airDate) =>
        new() { EpisodeNumber = number, AirDate = airDate };

    // --- IsRecent ---

    [Fact]
    public void IsRecent_PastWithinWindow_IsTrue()
    {
        Assert.True(TmdbDateDisplay.IsRecent(Raw(-5), Today));
        Assert.True(TmdbDateDisplay.IsRecent(Raw(0), Today));   // today counts
        Assert.True(TmdbDateDisplay.IsRecent(Raw(-30), Today)); // inclusive boundary
    }

    [Fact]
    public void IsRecent_BeyondWindow_IsFalse() =>
        Assert.False(TmdbDateDisplay.IsRecent(Raw(-31), Today));

    [Fact]
    public void IsRecent_FutureDate_IsFalse() // pins #192/2a
    {
        Assert.False(TmdbDateDisplay.IsRecent(Raw(1), Today));
        Assert.False(TmdbDateDisplay.IsRecent(Raw(60), Today)); // unreleased movie ReleaseDate
    }

    [Fact]
    public void IsRecent_NullOrUnparseable_IsFalse()
    {
        Assert.False(TmdbDateDisplay.IsRecent(null, Today));
        Assert.False(TmdbDateDisplay.IsRecent("not-a-date", Today));
    }

    // --- FormatRelative ---

    [Fact]
    public void FormatRelative_NearFuture_IsTomorrow_NotToday() // pins #192/2b
    {
        Assert.Equal("tomorrow", TmdbDateDisplay.FormatRelative(Raw(1), Today));
        Assert.Equal("today", TmdbDateDisplay.FormatRelative(Raw(0), Today));
    }

    [Fact]
    public void FormatRelative_WithinWeek_UsesRelativePhrasing()
    {
        Assert.Equal("in 3 days", TmdbDateDisplay.FormatRelative(Raw(3), Today));
        Assert.Equal("yesterday", TmdbDateDisplay.FormatRelative(Raw(-1), Today));
        Assert.Equal("5 days ago", TmdbDateDisplay.FormatRelative(Raw(-5), Today));
        Assert.Equal("1 week ago", TmdbDateDisplay.FormatRelative(Raw(-8), Today));   // singular, not "1 weeks ago"
        Assert.Equal("2 weeks ago", TmdbDateDisplay.FormatRelative(Raw(-14), Today));
    }

    [Fact]
    public void FormatRelative_OutsideWindow_FallsBackToAbsolute()
    {
        // compare against the helper's own culture-dependent format so the test is culture-stable
        Assert.Equal(Today.AddDays(8).ToString("MMM d, yyyy"), TmdbDateDisplay.FormatRelative(Raw(8), Today));
        Assert.Equal(Today.AddDays(-40).ToString("MMM d, yyyy"), TmdbDateDisplay.FormatRelative(Raw(-40), Today));
    }

    [Fact]
    public void FormatRelative_Unparseable_IsEmpty() =>
        Assert.Equal("", TmdbDateDisplay.FormatRelative("0000-00-00", Today));

    // --- FormatAbsolute ---

    [Fact]
    public void FormatAbsolute_Valid_FormatsDate() =>
        Assert.Equal(new DateTime(2026, 6, 11).ToString("MMM d, yyyy"), TmdbDateDisplay.FormatAbsolute("2026-06-11"));

    [Fact]
    public void FormatAbsolute_MissingOrSentinel_IsEmpty()
    {
        Assert.Equal("", TmdbDateDisplay.FormatAbsolute(null));
        Assert.Equal("", TmdbDateDisplay.FormatAbsolute("0000-00-00"));
    }

    // --- SplitAiredUpcoming ---

    [Fact]
    public void SplitAiredUpcoming_PartitionsOnTodayInclusive_AndPicksLowestFutureEpisode()
    {
        List<EpisodeInfo> episodes =
        [
            Ep(1, Raw(-10)),
            Ep(2, Raw(0)),   // today → aired
            Ep(4, Raw(5)),   // future
            Ep(3, Raw(2)),   // earlier future, lower episode number → NextUp
        ];

        (IReadOnlyList<EpisodeInfo> aired, EpisodeInfo? nextUp) = TmdbDateDisplay.SplitAiredUpcoming(episodes, Today);

        Assert.Equal([1, 2], aired.Select(e => e.EpisodeNumber).OrderBy(n => n));
        Assert.NotNull(nextUp);
        Assert.Equal(3, nextUp!.EpisodeNumber);
    }

    [Fact]
    public void SplitAiredUpcoming_DropsUnparseable_FromBoth()
    {
        List<EpisodeInfo> episodes = [Ep(1, Raw(-1)), Ep(2, null), Ep(3, "0000-00-00")];

        (IReadOnlyList<EpisodeInfo> aired, EpisodeInfo? nextUp) = TmdbDateDisplay.SplitAiredUpcoming(episodes, Today);

        Assert.Equal([1], aired.Select(e => e.EpisodeNumber));
        Assert.Null(nextUp);
    }

    [Fact]
    public void SplitAiredUpcoming_AllPast_HasNoNextUp()
    {
        List<EpisodeInfo> episodes = [Ep(1, Raw(-3)), Ep(2, Raw(-1))];

        (IReadOnlyList<EpisodeInfo> aired, EpisodeInfo? nextUp) = TmdbDateDisplay.SplitAiredUpcoming(episodes, Today);

        Assert.Equal(2, aired.Count);
        Assert.Null(nextUp);
    }
}
