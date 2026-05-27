using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// pins #82 — pure helpers in QueueItemExtensions had zero coverage. The Days* helpers
// normalize the UTC-Kind drift that surfaced in #49/#70; the AvailableOn helpers gate
// every streaming-service logo rendered on a poster card.
public class QueueItemExtensionsTests
{
    private static readonly DateTime TodayUtc = new(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DaysUntilNextEpisode_PositiveOffset_ReturnsDayCount()
    {
        QueueItem item = new() { NextEpisodeDate = TodayUtc.AddDays(3) };
        Assert.Equal(3, item.DaysUntilNextEpisode(TodayUtc));
    }

    [Fact]
    public void DaysUntilNextEpisode_SameDay_ReturnsZero()
    {
        QueueItem item = new() { NextEpisodeDate = TodayUtc };
        Assert.Equal(0, item.DaysUntilNextEpisode(TodayUtc));
    }

    // a wrong-sign (past) NextEpisodeDate is the #49/#70 failure shape — must surface
    // as a negative integer so predicate writers can guard against it explicitly.
    [Fact]
    public void DaysUntilNextEpisode_NegativeOffset_ReturnsNegativeDayCount()
    {
        QueueItem item = new() { NextEpisodeDate = TodayUtc.AddDays(-2) };
        Assert.Equal(-2, item.DaysUntilNextEpisode(TodayUtc));
    }

    [Fact]
    public void DaysUntilNextEpisode_Null_ReturnsNull()
    {
        QueueItem item = new() { NextEpisodeDate = null };
        Assert.Null(item.DaysUntilNextEpisode(TodayUtc));
    }

    [Fact]
    public void DaysSinceLatestEpisode_PositiveOffset_ReturnsDayCount()
    {
        QueueItem item = new() { LatestEpisodeDate = TodayUtc.AddDays(-3) };
        Assert.Equal(3, item.DaysSinceLatestEpisode(TodayUtc));
    }

    [Fact]
    public void DaysSinceLatestEpisode_SameDay_ReturnsZero()
    {
        QueueItem item = new() { LatestEpisodeDate = TodayUtc };
        Assert.Equal(0, item.DaysSinceLatestEpisode(TodayUtc));
    }

    // future-stamped LatestEpisodeDate (sibling of #86) must surface as a negative
    // day-diff — the wrong-sign value is the leak path for the typed-comparison rule.
    [Fact]
    public void DaysSinceLatestEpisode_NegativeOffset_ReturnsNegativeDayCount()
    {
        QueueItem item = new() { LatestEpisodeDate = TodayUtc.AddDays(2) };
        Assert.Equal(-2, item.DaysSinceLatestEpisode(TodayUtc));
    }

    [Fact]
    public void DaysSinceLatestEpisode_Null_ReturnsNull()
    {
        QueueItem item = new() { LatestEpisodeDate = null };
        Assert.Null(item.DaysSinceLatestEpisode(TodayUtc));
    }

    [Fact]
    public void ParseAvailableOnProviderIds_ValidJsonList_ReturnsIds()
    {
        QueueItem item = new() { AvailableOnJson = "[8,9,15]" };
        Assert.Equal(new[] { 8, 9, 15 }, item.ParseAvailableOnProviderIds());
    }

    [Fact]
    public void ParseAvailableOnProviderIds_SingleIntPayload_ReturnsSingletonList()
    {
        QueueItem item = new() { AvailableOnJson = "[15]" };
        Assert.Equal(new[] { 15 }, item.ParseAvailableOnProviderIds());
    }

    [Fact]
    public void ParseAvailableOnProviderIds_EmptyString_ReturnsEmptyList()
    {
        QueueItem item = new() { AvailableOnJson = "" };
        Assert.Empty(item.ParseAvailableOnProviderIds());
    }

    // malformed payload must surface as an empty list, not a crash — the parser is
    // best-effort because AvailableOnJson lands here from a Postgres text column.
    [Fact]
    public void ParseAvailableOnProviderIds_MalformedJson_ReturnsEmptyList()
    {
        QueueItem item = new() { AvailableOnJson = "not json" };
        Assert.Empty(item.ParseAvailableOnProviderIds());
    }

    // #141: a title with no stored provider data is "unknown — don't hide", mirroring the
    // API's IsWatchableOn — otherwise a queued show whose providers TMDb hasn't surfaced
    // yet vanishes from the Home watchlist rows.
    [Fact]
    public void IsStreamableOn_EmptyProviderData_ReturnsTrue()
    {
        QueueItem item = new() { AvailableOnJson = "[]" };
        Assert.True(item.IsStreamableOn([8]));
    }

    [Fact]
    public void IsStreamableOn_MalformedProviderData_ReturnsTrue()
    {
        QueueItem item = new() { AvailableOnJson = "not json" };
        Assert.True(item.IsStreamableOn([8]));
    }

    [Fact]
    public void IsStreamableOn_ProviderInUserServices_ReturnsTrue()
    {
        QueueItem item = new() { AvailableOnJson = "[8,15]" };
        Assert.True(item.IsStreamableOn([8]));
    }

    // a title with KNOWN providers that don't intersect the user's services is still
    // hidden — that's the filter's purpose; only the empty/unknown case changed for #141.
    [Fact]
    public void IsStreamableOn_KnownProvider_NotInUserServices_ReturnsFalse()
    {
        QueueItem item = new() { AvailableOnJson = "[15]" };
        Assert.False(item.IsStreamableOn([8]));
    }

    [Fact]
    public void IsStreamableOn_NoUserServices_ReturnsFalse()
    {
        QueueItem item = new() { AvailableOnJson = "[]" };
        Assert.False(item.IsStreamableOn([]));
    }

    [Fact]
    public void VisibleServicesFor_EmptyUserServices_ReturnsEmpty()
    {
        QueueItem item = new() { AvailableOnJson = "[8,9]" };
        Assert.Empty(item.VisibleServicesFor([]));
    }

    // 999 is not a known TMDb provider; even if a user "subscribes" to it the result
    // must skip it rather than try to render an unknown logo.
    [Fact]
    public void VisibleServicesFor_UnknownProviderIds_AreSkipped()
    {
        QueueItem item = new() { AvailableOnJson = "[999,8]" };
        List<StreamingService> visible = item.VisibleServicesFor([999, 8]);
        Assert.Single(visible);
        Assert.Equal(8, visible[0].TmdbProviderId);
    }

    [Fact]
    public void VisibleServicesFor_MaxCap_LimitsResultLength()
    {
        QueueItem item = new() { AvailableOnJson = "[8,9,15]" };
        List<StreamingService> visible = item.VisibleServicesFor([8, 9, 15], max: 2);
        Assert.Equal(2, visible.Count);
    }

    // AvailableOnJson stores provider IDs in the order TMDb returned; the result
    // must mirror that order, NOT the userServices ordering. Pins the implicit
    // contract the implementation's `foreach (int id in parsed)` encodes.
    [Fact]
    public void VisibleServicesFor_PreservesProviderIdOrderFromQueueItem()
    {
        QueueItem item = new() { AvailableOnJson = "[9,8]" };
        List<StreamingService> visible = item.VisibleServicesFor([8, 9]);
        int[] actualIds = [.. visible.Select(s => s.TmdbProviderId)];
        Assert.Equal([9, 8], actualIds);
    }
}
