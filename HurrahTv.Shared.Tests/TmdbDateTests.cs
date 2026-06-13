using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// TMDb air_date / first_air_date are date-only wire strings. The canonical parse must yield
// Kind=Utc so Api and Client agree on the convention — a bare DateTime.TryParse yields
// Kind=Unspecified, the calendar-day drift this helper exists to prevent. Pins #190.
public class TmdbDateTests
{
    [Fact]
    public void TryParse_DateOnly_YieldsUtcKind()
    {
        Assert.True(TmdbDate.TryParse("2026-06-11", out DateTime parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void TryParse_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(TmdbDate.TryParse(null, out _));
        Assert.False(TmdbDate.TryParse("", out _));
    }

    [Fact]
    public void TryParse_Garbage_ReturnsFalse() =>
        Assert.False(TmdbDate.TryParse("not-a-date", out _));

    [Fact]
    public void TryParse_OffsetTimestamp_NormalizesToUtc()
    {
        // a value with an offset must adjust to UTC, not stay local — keeps the convention
        // consistent if TMDb ever sends a full timestamp on these fields.
        Assert.True(TmdbDate.TryParse("2026-06-11T20:00:00-04:00", out DateTime parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc), parsed);
    }
}
