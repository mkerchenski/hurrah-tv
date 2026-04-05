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

        // get AI-curated rows for the home page
        group.MapGet("/rows", async (ClaimsPrincipal user, DbService db, CurationService curation, TmdbService tmdb, ILogger<CurationService> logger) =>
        {
            try
            {
                string userId = user.GetUserId();
                Task<List<QueueItem>> watchlistTask = db.GetQueueAsync(userId);
                Task<List<int>> providerTask = db.GetUserServicesAsync(userId);
                Task<UserSettings> settingsTask = db.GetUserSettingsAsync(userId);
                await Task.WhenAll(watchlistTask, providerTask, settingsTask);

                List<QueueItem> watchlist = watchlistTask.Result;
                List<int> providerIds = providerTask.Result;
                CurationResult result = await curation.GetCuratedRowsAsync(userId, watchlist, providerIds, settingsTask.Result.EnglishOnly);

                if (result.Rows.Count > 0)
                {
                    HashSet<int> excludeIds = [.. watchlist.Select(i => i.TmdbId)];
                    excludeIds.UnionWith(await db.GetDismissalsAsync(userId));
                    ExcludeShows(result, excludeIds);
                }

                List<CuratedRowResponse> rows = await ResolveRowsAsync(result.Rows, providerIds, tmdb);

                return Results.Ok(new CurationResponse
                {
                    Rows = rows,
                    FromCache = result.FromCache,
                    WatchlistChanged = result.WatchlistChanged,
                    AiEnabled = curation.IsEnabled,
                    Error = result.Error,
                    CandidateCount = result.CandidateCount
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Curation rows endpoint failed");
                return Results.Ok(new CurationResponse
                {
                    AiEnabled = curation.IsEnabled,
                    Error = "Curation temporarily unavailable"
                });
            }
        });

        // force refresh AI curation (rate-limited per user)
        group.MapPost("/refresh", async (ClaimsPrincipal user, DbService db, CurationService curation,
            TmdbService tmdb, IMemoryCache cache, ILogger<CurationService> logger) =>
        {
            try
            {
                string userId = user.GetUserId();
                string rateLimitKey = $"refresh-limit:{userId}";

                if (cache.TryGetValue(rateLimitKey, out _))
                    return Results.Ok(new CurationResponse { AiEnabled = curation.IsEnabled, Error = "Please wait before refreshing again" });

                cache.Set(rateLimitKey, true, RefreshCooldown);
                await db.SetCurationCacheAsync(userId, "[]", "force-refresh");

                Task<List<QueueItem>> watchlistTask = db.GetQueueAsync(userId);
                Task<List<int>> providerTask = db.GetUserServicesAsync(userId);
                Task<UserSettings> settingsTask = db.GetUserSettingsAsync(userId);
                await Task.WhenAll(watchlistTask, providerTask, settingsTask);

                List<QueueItem> watchlist = watchlistTask.Result;
                List<int> providerIds = providerTask.Result;
                CurationResult result = await curation.GetCuratedRowsAsync(userId, watchlist, providerIds, settingsTask.Result.EnglishOnly);

                if (result.Rows.Count > 0)
                {
                    HashSet<int> excludeIds = [.. watchlist.Select(i => i.TmdbId)];
                    excludeIds.UnionWith(await db.GetDismissalsAsync(userId));
                    ExcludeShows(result, excludeIds);
                }

                List<CuratedRowResponse> rows = await ResolveRowsAsync(result.Rows, providerIds, tmdb);

                return Results.Ok(new CurationResponse
                {
                    Rows = rows,
                    FromCache = false,
                    WatchlistChanged = true,
                    AiEnabled = curation.IsEnabled
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Curation refresh endpoint failed");
                return Results.Ok(new CurationResponse
                {
                    AiEnabled = curation.IsEnabled,
                    Error = "Refresh failed, try again later"
                });
            }
        });

        // personalized match for a specific show
        group.MapGet("/match/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId,
            ClaimsPrincipal user, DbService db, CurationService curation, TmdbService tmdb,
            IMemoryCache cache, ILogger<CurationService> logger) =>
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

                ShowDetails? show = await tmdb.GetDetailsAsync(tmdbId, mediaType);
                if (show == null) return Results.NotFound();

                List<QueueItem> watchlist = await db.GetQueueAsync(userId);
                QueueItem? queueItem = watchlist.FirstOrDefault(i => i.TmdbId == tmdbId && i.MediaType == mediaType);

                // include episode sentiments if the user has any for this show
                ShowSentiments? showSentiments = mediaType == "tv"
                    ? await db.GetShowSentimentsAsync(tmdbId, userId)
                    : null;

                ShowMatchResult? result = await curation.GetShowMatchAsync(userId, show, watchlist, showSentiments, queueItem);

                if (result is not null)
                    cache.Set(cacheKey, result, TimeSpan.FromHours(12));

                return Results.Ok(result);
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

    private static void ExcludeShows(CurationResult result, HashSet<int> excludeIds)
    {
        foreach (AICuratedRow row in result.Rows)
            row.TmdbIds = [.. row.TmdbIds.Where(id => !excludeIds.Contains(id))];
    }

    private static async Task<List<CuratedRowResponse>> ResolveRowsAsync(
        List<AICuratedRow> aiRows, List<int> providerIds, TmdbService tmdb)
    {
        List<CuratedRowResponse> rows = [];
        HashSet<int> providerSet = [.. providerIds];

        foreach (AICuratedRow aiRow in aiRows)
        {
            Task<ShowDetails?>[] tasks = [.. aiRow.TmdbIds.Select(async tmdbId =>
            {
                ShowDetails? details = await tmdb.GetDetailsAsync(tmdbId, MediaTypes.Tv);
                details ??= await tmdb.GetDetailsAsync(tmdbId, MediaTypes.Movie);

                if (details != null)
                {
                    List<AvailableService> providers = await tmdb.GetWatchProvidersAsync(tmdbId, details.MediaType);
                    details.AvailableOn = [.. providers.Where(p => (p.Type is ProviderType.Flatrate or ProviderType.Ads) && providerSet.Contains(p.ProviderId))];
                }
                return details;
            })];

            ShowDetails?[] resolved = await Task.WhenAll(tasks);
            List<SearchResult> results = [.. resolved.Where(d => d != null).Cast<SearchResult>()];

            if (results.Count > 0)
            {
                rows.Add(new CuratedRowResponse
                {
                    Title = aiRow.Title,
                    Subtitle = aiRow.Subtitle,
                    Results = results,
                    Reasons = aiRow.Reasons
                });
            }
        }

        return rows;
    }
}
