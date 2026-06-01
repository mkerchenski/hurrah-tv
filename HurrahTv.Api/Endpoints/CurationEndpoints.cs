using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using HurrahTv.Api.Services;
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
            TmdbService tmdb, IMemoryCache cache, ILogger<CurationService> logger, bool? refresh, CancellationToken ct) =>
        {
            try
            {
                string userId = user.GetUserId();

                // a shuffle always advances the pick (free); only the paid reservoir regen is
                // rate-limited, so a second shuffle inside the cooldown still moves to a new pick.
                bool doRefresh = refresh == true;
                bool regenerate = false;
                if (doRefresh && !cache.TryGetValue($"refresh-limit:{userId}", out _))
                {
                    cache.Set($"refresh-limit:{userId}", true, RefreshCooldown);
                    regenerate = true;
                }

                // batch services + genres + settings in one parallel fetch (GetUserPreferencesAsync)
                // alongside the queue, so the genre fetch isn't serialized in front of the TMDb fan-out.
                Task<List<QueueItem>> watchlistTask = db.GetQueueAsync(userId, ct);
                Task<DbService.UserPreferences> prefsTask = db.GetUserPreferencesAsync(userId, ct);
                await Task.WhenAll(watchlistTask, prefsTask);

                DbService.UserPreferences prefs = prefsTask.Result;
                List<int> providerIds = prefs.ProviderIds;
                HeroResult result = await curation.GetCuratedHeroAsync(userId, watchlistTask.Result, providerIds, prefs.GenreIds,
                    prefs.EnglishOnly, regenerateReservoir: regenerate, advancePick: doRefresh, cancellationToken: ct);

                CuratedHero? hero = await ResolveHeroAsync(result, providerIds, tmdb, ct);

                // record the impression only after the pick hydrated, so a TMDb miss can't burn a
                // strong pick's 14-day cooldown without ever showing it. ShouldRecordImpression is
                // false when it's already today's pick, avoiding a redundant write per page load.
                if (hero is not null && result.ShouldRecordImpression)
                    await db.RecordHeroImpressionAsync(userId, result.TmdbId!.Value, result.MediaType, ct);

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
