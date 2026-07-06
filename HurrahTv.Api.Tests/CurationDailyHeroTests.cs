using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// pins #229 — the precomputed daily hero is persisted so /api/curation/hero is a keyed read.
// These tests cover the DbService round-trip (write → read-back → overwrite) and the
// per-(user, media type) keying. The freshness *decision* (ForDate == today && hash matches)
// lives in HurrahTv.Shared.Curation.DailyHeroFreshness and is unit-tested there.
[Collection("postgres")]
public class CurationDailyHeroTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task WriteThenGet_RoundTripsAllColumns()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        DateOnly forDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await db.SetDailyHeroAsync("hero-user", MediaTypes.All, forDate, "hash-abc", """{"tmdbId":1396}""", tmdbId: 1396);

        (string heroJson, DateOnly forDate, string watchlistHash, int tmdbId)? row =
            await db.GetDailyHeroAsync("hero-user", MediaTypes.All);
        Assert.NotNull(row);
        Assert.Equal("""{"tmdbId":1396}""", row.Value.heroJson);
        Assert.Equal(forDate, row.Value.forDate);
        Assert.Equal("hash-abc", row.Value.watchlistHash);
        Assert.Equal(1396, row.Value.tmdbId);
    }

    [Fact]
    public async Task Set_IsUpsert_OnePerUserMediaType_AndOverwrites()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        DateOnly day1 = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly day2 = day1.AddDays(1);

        await db.SetDailyHeroAsync("hero-user", MediaTypes.All, day1, "hash-1", """{"tmdbId":100}""", tmdbId: 100);
        await db.SetDailyHeroAsync("hero-user", MediaTypes.All, day2, "hash-2", """{"tmdbId":200}""", tmdbId: 200);

        // the second write overwrites the first — one row per (user, media type)
        (string heroJson, DateOnly forDate, string watchlistHash, int tmdbId)? row =
            await db.GetDailyHeroAsync("hero-user", MediaTypes.All);
        Assert.NotNull(row);
        Assert.Equal(day2, row.Value.forDate);
        Assert.Equal("hash-2", row.Value.watchlistHash);
        Assert.Equal(200, row.Value.tmdbId);
    }

    [Fact]
    public async Task SameUser_DifferentMediaType_AreSeparateRows()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        DateOnly forDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await db.SetDailyHeroAsync("hero-user", MediaTypes.Tv, forDate, "hash-tv", """{"tmdbId":1399}""", tmdbId: 1399);
        await db.SetDailyHeroAsync("hero-user", MediaTypes.Movie, forDate, "hash-mv", """{"tmdbId":603}""", tmdbId: 603);

        Assert.Equal(1399, (await db.GetDailyHeroAsync("hero-user", MediaTypes.Tv))!.Value.tmdbId);
        Assert.Equal(603, (await db.GetDailyHeroAsync("hero-user", MediaTypes.Movie))!.Value.tmdbId);
    }

    [Fact]
    public async Task Get_IsScopedPerUser_AndReturnsNullWhenAbsent()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        DateOnly forDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await db.SetDailyHeroAsync("user-a", MediaTypes.All, forDate, "hash-a", """{"tmdbId":1}""", tmdbId: 1);

        Assert.NotNull(await db.GetDailyHeroAsync("user-a", MediaTypes.All));
        Assert.Null(await db.GetDailyHeroAsync("user-b", MediaTypes.All));   // absent → null (compute on demand)
    }
}
