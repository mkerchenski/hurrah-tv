using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// pins #135 — the hero rotation cooldown depends on per-user last-shown timestamps.
// These tests cover the DbService round-trip (record → read-back) and the upsert
// behavior that keeps "last shown" a single row per (user, title, media type). The
// cooldown *decision* logic lives in HurrahTv.Shared.HeroSelector and is unit-tested there.
//
// Keyed by (TmdbId, MediaType) since #146 — a movie and a TV show can share a numeric id.
[Collection("postgres")]
public class CurationHeroImpressionsTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RecordThenGet_ReturnsTheImpression()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        DateTime before = DateTime.UtcNow.AddSeconds(-5);

        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1396, MediaTypes.Tv);

        Dictionary<(int, string), DateTime> impressions = await db.GetHeroImpressionsAsync("hero-user");
        Assert.True(impressions.ContainsKey((1396, MediaTypes.Tv)));
        Assert.True(impressions[(1396, MediaTypes.Tv)] >= before, "ShownAt should be set to roughly now");
    }

    [Fact]
    public async Task Record_IsUpsert_OnePerTitle_AndAdvancesShownAt()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1396, MediaTypes.Tv);
        DateTime first = (await db.GetHeroImpressionsAsync("hero-user"))[(1396, MediaTypes.Tv)];

        // small gap so the second NOW() is observably later
        await Task.Delay(20);
        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1396, MediaTypes.Tv);

        Dictionary<(int, string), DateTime> impressions = await db.GetHeroImpressionsAsync("hero-user");
        Assert.Single(impressions);                              // still one row for the title
        Assert.True(impressions[(1396, MediaTypes.Tv)] >= first); // ShownAt advanced on conflict
    }

    [Fact]
    public async Task Get_IsScopedPerUser()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        await db.RecordHeroImpressionAsync("user-a", tmdbId: 100, MediaTypes.Tv);
        await db.RecordHeroImpressionAsync("user-b", tmdbId: 200, MediaTypes.Tv);

        Dictionary<(int, string), DateTime> a = await db.GetHeroImpressionsAsync("user-a");
        Assert.True(a.ContainsKey((100, MediaTypes.Tv)));
        Assert.False(a.ContainsKey((200, MediaTypes.Tv)));
    }

    // pins #146 — a movie and a TV show sharing a numeric id are tracked as separate rows,
    // so featuring one never burns the other's cooldown.
    [Fact]
    public async Task SameTmdbId_DifferentMediaType_AreSeparateRows()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1399, MediaTypes.Tv);
        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1399, MediaTypes.Movie);

        Dictionary<(int, string), DateTime> impressions = await db.GetHeroImpressionsAsync("hero-user");
        Assert.Equal(2, impressions.Count);
        Assert.True(impressions.ContainsKey((1399, MediaTypes.Tv)));
        Assert.True(impressions.ContainsKey((1399, MediaTypes.Movie)));
    }
}
