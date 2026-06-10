using System.Net;
using System.Text;
using HurrahTv.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace HurrahTv.Api.Tests;

// pins #189 — GetEpisodeDatesAsync re-derives latest/next from live season episodes when a
// show is actively airing, because TMDb's last_episode_to_air lags the season data the Details
// browser reads for daily shows. No Postgres / WebApplicationFactory — TmdbService is exercised
// directly with a routing fake handler + a real MemoryCache.
public class TmdbEpisodeDatesSeasonReconcileTests
{
    private const int TmdbId = 123;

    // routes by URL substring to canned JSON; records whether the season endpoint was hit so the
    // recency-gate test can assert the extra fetch is skipped for dormant shows.
    private sealed class RoutingHandler(string showJson, string? seasonJson) : HttpMessageHandler
    {
        public bool SeasonRequested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.Contains("/season/"))
            {
                SeasonRequested = true;
                body = seasonJson ?? "{}";
            }
            else
            {
                body = showJson;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static TmdbService BuildService(RoutingHandler handler)
    {
        HttpClient http = new(handler);
        IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tmdb:ApiKey"] = "test-key" })
            .Build();
        return new TmdbService(http, cache, config);
    }

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd");

    // the daily-show repro: last_episode_to_air lags at S12E1 (4 days ago), but the live season
    // has a newer aired S12E2 (1 day ago) plus an upcoming S12E3 — latest/next must reconcile to
    // the season data so Home's "X days ago" matches Details.
    [Fact]
    public async Task GetEpisodeDates_ActiveShow_RederivesLatestAndNextFromSeason()
    {
        DateTime today = DateTime.UtcNow.Date;
        string showJson = $$"""
        {
          "last_episode_to_air": { "air_date": "{{Iso(today.AddDays(-4))}}", "season_number": 12, "episode_number": 1 },
          "next_episode_to_air": null
        }
        """;
        string seasonJson = $$"""
        {
          "name": "Season 12",
          "episodes": [
            { "episode_number": 1, "air_date": "{{Iso(today.AddDays(-4))}}" },
            { "episode_number": 2, "air_date": "{{Iso(today.AddDays(-1))}}" },
            { "episode_number": 3, "air_date": "{{Iso(today.AddDays(6))}}" }
          ]
        }
        """;

        RoutingHandler handler = new(showJson, seasonJson);
        TmdbService tmdb = BuildService(handler);

        (DateTime? lastAired, int? lastSeason, int? lastEpisode, DateTime? nextAir, int? nextSeason, int? nextEpisode)
            = await tmdb.GetEpisodeDatesAsync(TmdbId);

        Assert.True(handler.SeasonRequested);
        Assert.Equal(today.AddDays(-1), lastAired!.Value.Date);
        Assert.Equal(12, lastSeason);
        Assert.Equal(2, lastEpisode);
        Assert.Equal(today.AddDays(6), nextAir!.Value.Date);
        Assert.Equal(12, nextSeason);
        Assert.Equal(3, nextEpisode);
    }

    // dormant show (latest aired well outside the ~10-day active window): the season fetch is
    // skipped entirely and the stored values stay as last_episode_to_air reported them.
    [Fact]
    public async Task GetEpisodeDates_DormantShow_SkipsSeasonFetch()
    {
        DateTime today = DateTime.UtcNow.Date;
        string showJson = $$"""
        {
          "last_episode_to_air": { "air_date": "{{Iso(today.AddDays(-40))}}", "season_number": 3, "episode_number": 10 },
          "next_episode_to_air": null
        }
        """;

        RoutingHandler handler = new(showJson, seasonJson: null);
        TmdbService tmdb = BuildService(handler);

        (DateTime? lastAired, int? lastSeason, int? lastEpisode, DateTime? nextAir, _, _)
            = await tmdb.GetEpisodeDatesAsync(TmdbId);

        Assert.False(handler.SeasonRequested);
        Assert.Equal(today.AddDays(-40), lastAired!.Value.Date);
        Assert.Equal(3, lastSeason);
        Assert.Equal(10, lastEpisode);
        Assert.Null(nextAir);
    }

    // pins the same-air-date tie-break (xreview/Copilot): last_episode_to_air lags at E1 while
    // a SECOND episode (E2) aired the SAME day. The date isn't strictly newer, so a date-only
    // override gate would keep E1 — but the newest aired is really E2. Latest episode number
    // must reconcile to E2 even though the dates match.
    [Fact]
    public async Task GetEpisodeDates_TwoEpisodesSameDay_RederivesHigherEpisodeNumber()
    {
        DateTime today = DateTime.UtcNow.Date;
        string showJson = $$"""
        {
          "last_episode_to_air": { "air_date": "{{Iso(today)}}", "season_number": 13, "episode_number": 1 },
          "next_episode_to_air": null
        }
        """;
        string seasonJson = $$"""
        {
          "name": "Season 13",
          "episodes": [
            { "episode_number": 1, "air_date": "{{Iso(today)}}" },
            { "episode_number": 2, "air_date": "{{Iso(today)}}" }
          ]
        }
        """;

        RoutingHandler handler = new(showJson, seasonJson);
        TmdbService tmdb = BuildService(handler);

        (DateTime? lastAired, int? lastSeason, int? lastEpisode, _, _, _)
            = await tmdb.GetEpisodeDatesAsync(TmdbId);

        Assert.Equal(today, lastAired!.Value.Date);
        Assert.Equal(13, lastSeason);
        Assert.Equal(2, lastEpisode);
    }
}
