using System.Net.Http.Json;
using System.Text.Json;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// pins #229 — GET /api/curation/hero serves a persisted daily hero as a keyed read. The fast
// path runs BEFORE the AI gate, so these exercise it end-to-end even though the test host runs
// with AI disabled: a fresh row is served without AI, a stale row forces recompute (which, with
// AI off, yields no pick), and a pick the user already has is skipped by the safety-net.
[Collection("postgres")]
public class CurationDailyHeroEndpointTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string HeroJson(int tmdbId, string mediaType, string title) =>
        // serialize with default options to match how the endpoint deserializes the stored blob
        JsonSerializer.Serialize(new CuratedHero
        {
            Result = new SearchResult { TmdbId = tmdbId, MediaType = mediaType, Title = title, BackdropPath = "/backdrop.jpg" },
            Reason = "Because you liked something",
            Score = 88
        });

    [Fact]
    public async Task Hero_ServesFreshPersistedRow_WithoutAI()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        string userId = "hero-user";
        // empty watchlist → the endpoint computes this exact hash from GetQueueAsync
        string hash = CurationService.ComputeWatchlistHash([]);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await db.SetDailyHeroAsync(userId, MediaTypes.All, today, hash, HeroJson(1396, MediaTypes.Tv, "Breaking Bad"), tmdbId: 1396);

        HttpClient client = TestAuth.CreateClient(fx, userId);
        CuratedHeroResponse? resp = await client.GetFromJsonAsync<CuratedHeroResponse>("/api/curation/hero");

        Assert.NotNull(resp);
        Assert.NotNull(resp!.Hero);
        Assert.Equal(1396, resp.Hero!.Result.TmdbId);
        Assert.Equal("Breaking Bad", resp.Hero.Result.Title);
    }

    [Fact]
    public async Task Hero_StaleRow_ForcesRecompute_NotServed()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        string userId = "hero-user";
        string hash = CurationService.ComputeWatchlistHash([]);
        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        // a row computed yesterday must not be served today — the daily rotation has to advance
        await db.SetDailyHeroAsync(userId, MediaTypes.All, yesterday, hash, HeroJson(1396, MediaTypes.Tv, "Breaking Bad"), tmdbId: 1396);

        HttpClient client = TestAuth.CreateClient(fx, userId);
        CuratedHeroResponse? resp = await client.GetFromJsonAsync<CuratedHeroResponse>("/api/curation/hero");

        // falls through to the compute path; AI is disabled in tests, so no pick is produced
        Assert.NotNull(resp);
        Assert.Null(resp!.Hero);
    }

    [Fact]
    public async Task Hero_ShufflePassesRefresh_SkipsKeyedRead()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        string userId = "hero-user";
        string hash = CurationService.ComputeWatchlistHash([]);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        // even a fresh row is bypassed on ?refresh=true (a shuffle must re-select, not re-serve)
        await db.SetDailyHeroAsync(userId, MediaTypes.All, today, hash, HeroJson(1396, MediaTypes.Tv, "Breaking Bad"), tmdbId: 1396);

        HttpClient client = TestAuth.CreateClient(fx, userId);
        CuratedHeroResponse? resp = await client.GetFromJsonAsync<CuratedHeroResponse>("/api/curation/hero?refresh=true");

        // shuffle skips the keyed read → compute path → AI off → no pick
        Assert.NotNull(resp);
        Assert.Null(resp!.Hero);
    }

    [Fact]
    public async Task Hero_SafetyNet_SkipsFreshRow_WhenPickIsAlreadyOnWatchlist()
    {
        DbService db = fx.Factory.Services.GetRequiredService<DbService>();
        string userId = "hero-user";

        // the user already has the would-be hero on their list
        await db.AddToQueueAsync(new QueueItem
        {
            TmdbId = 1396,
            MediaType = MediaTypes.Tv,
            Title = "Breaking Bad",
            PosterPath = "/poster.jpg",
            BackdropPath = "",
            Status = QueueStatus.WantToWatch,
            AvailableOnJson = "[]"
        }, userId);

        // hash the actual stored queue so the persisted row reads as fresh
        List<QueueItem> watchlist = await db.GetQueueAsync(userId);
        string hash = CurationService.ComputeWatchlistHash(watchlist);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        // fresh (today + matching hash) row for that same title — freshness passes, but the
        // safety-net must skip it because it's now on the watchlist, falling through to recompute
        await db.SetDailyHeroAsync(userId, MediaTypes.All, today, hash, HeroJson(1396, MediaTypes.Tv, "Breaking Bad"), tmdbId: 1396);

        HttpClient client = TestAuth.CreateClient(fx, userId);
        CuratedHeroResponse? resp = await client.GetFromJsonAsync<CuratedHeroResponse>("/api/curation/hero");

        // recompute path (AI off) → no pick, rather than serving a title the user already has
        Assert.NotNull(resp);
        Assert.Null(resp!.Hero);
    }
}
