using HurrahTv.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// pins #135 — the hero rotation cooldown depends on per-user last-shown timestamps.
// These tests cover the DbService round-trip (record → read-back) and the upsert
// behavior that keeps "last shown" a single row per (user, title). The cooldown
// *decision* logic lives in HurrahTv.Shared.HeroSelector and is unit-tested there.
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

        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1396);

        Dictionary<int, DateTime> impressions = await db.GetHeroImpressionsAsync("hero-user");
        Assert.True(impressions.ContainsKey(1396));
        Assert.True(impressions[1396] >= before, "ShownAt should be set to roughly now");
    }

    [Fact]
    public async Task Record_IsUpsert_OnePerTitle_AndAdvancesShownAt()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1396);
        DateTime first = (await db.GetHeroImpressionsAsync("hero-user"))[1396];

        // small gap so the second NOW() is observably later
        await Task.Delay(20);
        await db.RecordHeroImpressionAsync("hero-user", tmdbId: 1396);

        Dictionary<int, DateTime> impressions = await db.GetHeroImpressionsAsync("hero-user");
        Assert.Single(impressions);                 // still one row for the title
        Assert.True(impressions[1396] >= first);    // ShownAt advanced on conflict
    }

    [Fact]
    public async Task Get_IsScopedPerUser()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();

        await db.RecordHeroImpressionAsync("user-a", tmdbId: 100);
        await db.RecordHeroImpressionAsync("user-b", tmdbId: 200);

        Dictionary<int, DateTime> a = await db.GetHeroImpressionsAsync("user-a");
        Assert.True(a.ContainsKey(100));
        Assert.False(a.ContainsKey(200));
    }
}
