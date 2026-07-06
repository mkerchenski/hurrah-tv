using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Curation;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class CurationEndpoints
{
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(5);

    public static void MapCurationEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/curation").RequireAuthorization();

        // single rotating AI hero pick for the home page (replaces the curated-rows section).
        // ?refresh=true regenerates the reservoir and advances past today's pick (rate-limited).
        group.MapGet("/hero", async (ClaimsPrincipal user, DbService db, CurationService curation,
            TmdbService tmdb, IMemoryCache cache, ILogger<CurationService> logger, HttpContext http, bool? refresh, string? mediaType, CancellationToken ct) =>
        {
            try
            {
                string userId = user.GetUserId();

                // active Home media filter — narrows the cached reservoir to a movie/TV pick (#147).
                // anything other than a valid media type means "all" (no narrowing).
                string filter = MediaTypes.IsValid(mediaType) ? mediaType! : MediaTypes.All;

                // a shuffle always advances the pick (free); only the paid reservoir regen is
                // rate-limited, so a second shuffle inside the cooldown still moves to a new pick.
                bool doRefresh = refresh == true;
                bool regenerate = false;
                if (doRefresh && !cache.TryGetValue($"refresh-limit:{userId}", out _))
                {
                    cache.Set($"refresh-limit:{userId}", true, RefreshCooldown);
                    regenerate = true;
                }

                // attribute the hero's server cost by phase into Server-Timing so the #201 RUM
                // beacon / DevTools can split db vs curation (selection + paid regen) vs TMDb
                // hydration on the LCP path (#229 AC#1). Timings are appended below per phase.
                long tDb = Stopwatch.GetTimestamp();

                // batch services + genres + settings in one parallel fetch (GetUserPreferencesAsync)
                // alongside the queue, so the genre fetch isn't serialized in front of the TMDb fan-out.
                Task<List<QueueItem>> watchlistTask = db.GetQueueAsync(userId, ct);
                Task<DbService.UserPreferences> prefsTask = db.GetUserPreferencesAsync(userId, ct);
                await Task.WhenAll(watchlistTask, prefsTask);
                double dbMs = Stopwatch.GetElapsedTime(tDb).TotalMilliseconds;

                List<QueueItem> watchlist = watchlistTask.Result;
                DbService.UserPreferences prefs = prefsTask.Result;
                List<int> providerIds = prefs.ProviderIds;

                // the daily pick is deterministic and stable within a UTC day, keyed by the same
                // watchlist hash the reservoir caches by — so a warm load is a single keyed read of
                // the already-hydrated hero, skipping both selection and TMDb hydration (#229).
                string currentHash = CurationService.ComputeWatchlistHash(watchlist);
                DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

                if (!doRefresh)
                {
                    long tHit = Stopwatch.GetTimestamp();
                    (string heroJson, DateOnly forDate, string watchlistHash, int tmdbId)? daily =
                        await db.GetDailyHeroAsync(userId, filter, ct);
                    if (daily is { } d && DailyHeroFreshness.IsFresh(d.forDate, d.watchlistHash, currentHash, today))
                    {
                        CuratedHero? cachedHero = JsonSerializer.Deserialize<CuratedHero>(d.heroJson);
                        // safety-net: if the user added this title to their list since it was persisted,
                        // fall through to recompute rather than recommending something they already have.
                        bool onList = cachedHero is not null
                            && watchlist.Any(i => i.TmdbId == cachedHero.Result.TmdbId && i.MediaType == cachedHero.Result.MediaType);
                        if (cachedHero is not null && !onList)
                        {
                            double hitMs = Stopwatch.GetElapsedTime(tHit).TotalMilliseconds;
                            http.Response.Headers.Append("Server-Timing", $"hero-db;dur={dbMs:F1}, hero-daily-hit;dur={hitMs:F1}");
                            return Results.Ok(new CuratedHeroResponse { Hero = cachedHero, AiEnabled = curation.IsEnabled });
                        }
                    }
                }

                // miss (new day / watchlist change / no row) or shuffle: select + hydrate, then
                // persist the hydrated pick as today's hero so subsequent loads are keyed reads.
                long tCuration = Stopwatch.GetTimestamp();
                HeroResult result = await curation.GetCuratedHeroAsync(userId, watchlist, providerIds, prefs.GenreIds,
                    prefs.EnglishOnly, regenerateReservoir: regenerate, advancePick: doRefresh, mediaType: filter, cancellationToken: ct);
                double curationMs = Stopwatch.GetElapsedTime(tCuration).TotalMilliseconds;

                long tTmdb = Stopwatch.GetTimestamp();
                CuratedHero? hero = await ResolveHeroAsync(result, providerIds, tmdb, ct);
                double tmdbMs = Stopwatch.GetElapsedTime(tTmdb).TotalMilliseconds;

                if (hero is not null)
                {
                    await db.SetDailyHeroAsync(userId, filter, today, currentHash, JsonSerializer.Serialize(hero), result.TmdbId!.Value, ct);

                    // record the impression only after the pick hydrated, so a TMDb miss can't burn a
                    // strong pick's 14-day cooldown without ever showing it. ShouldRecordImpression is
                    // false when it's already today's pick, avoiding a redundant write per page load.
                    if (result.ShouldRecordImpression)
                        await db.RecordHeroImpressionAsync(userId, result.TmdbId!.Value, result.MediaType, ct);
                }

                string curationDesc = regenerate ? ";desc=\"regen\"" : "";
                http.Response.Headers.Append("Server-Timing",
                    $"hero-db;dur={dbMs:F1}, hero-curation;dur={curationMs:F1}{curationDesc}, hero-tmdb;dur={tmdbMs:F1}");

                return Results.Ok(new CuratedHeroResponse
                {
                    Hero = hero,
                    AiEnabled = curation.IsEnabled,
                    Error = result.Error
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Curation hero endpoint failed");
                return Results.Ok(new CuratedHeroResponse
                {
                    AiEnabled = curation.IsEnabled,
                    Error = "Curation temporarily unavailable"
                });
            }
        });

        // personalized match for a specific show
        group.MapGet("/match/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId,
            ClaimsPrincipal user, DbService db, CurationService curation, TmdbService tmdb,
            IMemoryCache cache, ILogger<CurationService> logger, CancellationToken ct) =>
        {
            try
            {
                if (!MediaTypes.IsValid(mediaType))
                    return Results.BadRequest("mediaType must be 'movie' or 'tv'");
                if (!curation.IsEnabled) return Results.Ok<ShowMatchResult?>(null);

                string userId = user.GetUserId();
                string cacheKey = $"match:{userId}:{mediaType}:{tmdbId}";

                if (cache.TryGetValue(cacheKey, out ShowMatchResult? cached))
                    return Results.Ok(cached);

                ShowDetails? show = await tmdb.GetDetailsAsync(tmdbId, mediaType, ct);
                if (show == null) return Results.NotFound();

                List<QueueItem> watchlist = await db.GetQueueAsync(userId, ct);
                QueueItem? queueItem = watchlist.FirstOrDefault(i => i.TmdbId == tmdbId && i.MediaType == mediaType);

                // include episode sentiments if the user has any for this show
                ShowSentiments? showSentiments = mediaType == "tv"
                    ? await db.GetShowSentimentsAsync(tmdbId, userId, ct)
                    : null;

                ShowMatchResult? result = await curation.GetShowMatchAsync(userId, show, watchlist, showSentiments, queueItem, ct);

                if (result is not null)
                    cache.Set(cacheKey, result, TimeSpan.FromHours(12));

                return Results.Ok(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 499 is a server-log signal, not a client contract. The cancelling client
                // already raised OCE locally (HttpClient.GetFromJsonAsync) and abandoned this
                // response — the 499 just lets request-log middleware bucket "client gave up"
                // separately from real 5xx errors. ApiClient.GetShowMatchAsync intentionally
                // does NOT special-case the status. See Learnings/status-499-server-log-only.md.
                // pins #117, #120.
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Match endpoint failed for {MediaType}/{TmdbId}", mediaType, tmdbId);
                return Results.Ok<ShowMatchResult?>(null);
            }
        });

        // ai cost stats
        group.MapGet("/usage", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            decimal userCost = await db.GetUserAICostAsync(userId);
            decimal monthlyCost = await db.GetMonthlyAICostAsync();
            return Results.Ok(new { userTotalCost = userCost, monthlyTotalCost = monthlyCost });
        });
    }

    // hydrate the chosen hero's TmdbId into a SearchResult with the user's service badges.
    private static async Task<CuratedHero?> ResolveHeroAsync(HeroResult result, List<int> providerIds, TmdbService tmdb, CancellationToken cancellationToken)
    {
        if (!result.HasPick) return null;

        ShowDetails? details = await tmdb.GetDetailsAsync(result.TmdbId!.Value, result.MediaType, cancellationToken);
        if (details == null) return null;

        HashSet<int> providerSet = [.. providerIds];
        List<AvailableService> providers = await tmdb.GetWatchProvidersAsync(result.TmdbId.Value, details.MediaType, cancellationToken);
        details.AvailableOn = [.. providers.Where(p => (p.Type is ProviderType.Flatrate or ProviderType.Ads) && providerSet.Contains(p.ProviderId))];

        return new CuratedHero { Result = details, Reason = result.Reason, Score = result.Score };
    }
}
