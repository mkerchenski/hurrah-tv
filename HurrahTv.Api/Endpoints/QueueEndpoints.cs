using System.Security.Claims;
using System.Text.Json;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class QueueEndpoints
{
    private static readonly TimeSpan EpisodeCheckStaleAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan ProviderCheckStaleAfter = TimeSpan.FromHours(24);

    public static void MapQueueEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/queue").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, DbService db, IServiceScopeFactory scopeFactory) =>
        {
            string userId = user.GetUserId();

            // run watched + services in parallel with the queue read so we don't pay them sequentially
            Task<List<WatchedEpisode>> watchedTask = db.GetWatchedEpisodesAsync(userId);
            Task<List<int>> activeServicesTask = db.GetUserServicesAsync(userId);

            List<QueueItem> items = await db.GetQueueAsync(userId);

            List<QueueItem> staleEpisodes = [.. items
                .Where(i => i.MediaType == MediaTypes.Tv
                    && i.Status is QueueStatus.Watching or QueueStatus.WantToWatch
                    && (i.LastEpisodeCheckAt == null
                        || DateTime.UtcNow - i.LastEpisodeCheckAt > EpisodeCheckStaleAfter
                        || i.LatestEpisodeSeason == null))];

            List<QueueItem> staleProviders = [.. items
                .Where(i => i.AvailableOnCheckedAt == null
                    || DateTime.UtcNow - i.AvailableOnCheckedAt > ProviderCheckStaleAfter)];

            // fire-and-forget the TMDb refresh — fresh episode/provider data shows up on the
            // next /api/queue call, but the current request returns immediately. IsWatchableOn
            // returns true for items with empty AvailableOnJson, so newly-added items are NOT
            // hidden during the pre-refresh window.
            if (staleEpisodes.Count > 0 || staleProviders.Count > 0)
                _ = RefreshStaleItemsInBackground(scopeFactory, staleEpisodes, staleProviders);

            HashSet<int> activeServiceIds = [.. await activeServicesTask];
            if (activeServiceIds.Count > 0)
                items = [.. items.Where(i => IsWatchableOn(i.AvailableOnJson, activeServiceIds))];

            return Results.Ok(new QueueResponse(items, await watchedTask));
        });

        group.MapPost("", async (QueueItem item, ClaimsPrincipal user, DbService db) =>
        {
            if (string.IsNullOrWhiteSpace(item.Title) || !MediaTypes.IsValid(item.MediaType) || item.TmdbId <= 0)
                return Results.BadRequest("Invalid queue item");
            // QueueStatus is non-contiguous (0, 1, 2, 4) — value 3 was removed. Reject anything undefined.
            if (!Enum.IsDefined(item.Status))
                return Results.BadRequest("Invalid status");

            string userId = user.GetUserId();
            QueueItem? added = await db.AddToQueueAsync(item, userId);
            return added != null ? Results.Created($"/api/queue/{added.Id}", added) : Results.Conflict("Already in queue");
        });

        group.MapDelete("/{id:int}", async (int id, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            bool removed = await db.RemoveFromQueueAsync(id, userId);
            return removed ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{id:int}/status", async (int id, QueueStatusUpdate update, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            QueueItem? updated = await db.UpdateStatusAsync(id, update.Status, userId);
            return updated is not null ? Results.Ok(updated) : Results.NotFound();
        });

        group.MapPut("/{id:int}/position", async (int id, PositionUpdate update, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            bool updated = await db.ReorderAsync(id, update.Position, userId);
            return updated ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{id:int}/sentiment", async (int id, SentimentUpdate update, ClaimsPrincipal user, DbService db) =>
        {
            if (!SentimentLevel.IsValid(update.Sentiment))
                return Results.BadRequest("Sentiment must be 1 (down), 2 (up), or 3 (favorite)");

            string userId = user.GetUserId();
            QueueItem? updated = await db.UpdateSentimentAsync(id, update.Sentiment, userId);
            return updated is not null ? Results.Ok(updated) : Results.NotFound();
        });

        group.MapPut("/{id:int}/progress", async (int id, ProgressUpdate update, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            QueueItem? updated = await db.UpdateProgressAsync(id, update.Season, update.Episode, userId);
            return updated is not null ? Results.Ok(updated) : Results.NotFound();
        });

        // "I've seen this" — adds as Finished or updates existing to Finished
        group.MapPost("/seen", async (SeenRequest request, ClaimsPrincipal user, DbService db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || !MediaTypes.IsValid(request.MediaType) || request.TmdbId <= 0)
                return Results.BadRequest("Invalid request");

            string userId = user.GetUserId();
            QueueItem? item = await db.MarkAsSeenAsync(
                request.TmdbId, request.MediaType, request.Title,
                request.PosterPath, request.BackdropPath, request.AvailableOnJson, userId);
            return Results.Ok(item);
        });

        // returns the queue item for this content, creating it as WantToWatch if absent.
        // never mutates an existing record — callers use the PUT endpoints to change status/sentiment.
        group.MapPost("/ensure", async (SeenRequest request, ClaimsPrincipal user, DbService db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || !MediaTypes.IsValid(request.MediaType) || request.TmdbId <= 0)
                return Results.BadRequest("Invalid request");

            string userId = user.GetUserId();
            QueueItem? item = await db.EnsureQueueItemAsync(
                request.TmdbId, request.MediaType, request.Title,
                request.PosterPath, request.BackdropPath, request.AvailableOnJson, userId);
            return item != null ? Results.Ok(item) : Results.Problem("Ensure failed");
        });

    }

    // detached from the request scope so the response can return before this finishes.
    // creates a fresh DI scope for transient services (e.g. TmdbService's HttpClient).
    // exceptions are swallowed at the per-item level; the next request will retry the items
    // that didn't get stamped with a fresh check timestamp.
    private static async Task RefreshStaleItemsInBackground(
        IServiceScopeFactory scopeFactory,
        List<QueueItem> staleEpisodes,
        List<QueueItem> staleProviders)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            DbService db = scope.ServiceProvider.GetRequiredService<DbService>();
            TmdbService tmdb = scope.ServiceProvider.GetRequiredService<TmdbService>();
            ILogger<DbService> logger = scope.ServiceProvider.GetRequiredService<ILogger<DbService>>();

            Task episodeRefresh = Task.WhenAll(staleEpisodes.Take(10).Select(async item =>
            {
                try
                {
                    (DateTime? lastAired, int? lastSeason, int? lastEp, DateTime? nextAir, int? nextSeason, int? nextEp)
                        = await tmdb.GetEpisodeDatesAsync(item.TmdbId);
                    await db.UpdateEpisodeDatesAsync(item.Id, lastAired, lastSeason, lastEp, nextAir, nextSeason, nextEp);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background refresh failed for episode dates {TmdbId}", item.TmdbId);
                }
            }));

            Task providerRefresh = Task.WhenAll(staleProviders.Take(10).Select(async item =>
            {
                try
                {
                    List<AvailableService> providers = await tmdb.GetWatchProvidersAsync(item.TmdbId, item.MediaType);
                    string json = JsonSerializer.Serialize(providers
                        .Where(p => p.Type is ProviderType.Flatrate or ProviderType.Ads)
                        .Select(p => p.ProviderId)
                        .Distinct());
                    await db.UpdateProvidersAsync(item.Id, json);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background refresh failed for providers {TmdbId}", item.TmdbId);
                }
            }));

            await Task.WhenAll(episodeRefresh, providerRefresh);
        }
        catch
        {
            // top-level safety net — fire-and-forget tasks must never crash the host
        }
    }

    private static bool IsWatchableOn(string availableOnJson, HashSet<int> activeServiceIds)
    {
        if (string.IsNullOrWhiteSpace(availableOnJson) || availableOnJson == "[]")
            return true; // unknown providers — don't hide
        try
        {
            List<int>? ids = JsonSerializer.Deserialize<List<int>>(availableOnJson);
            if (ids == null || ids.Count == 0) return true;
            return ids.Any(activeServiceIds.Contains);
        }
        catch (JsonException)
        {
            return true; // malformed payload — don't hide
        }
    }

    public record QueueStatusUpdate(QueueStatus Status);
    public record PositionUpdate(int Position);
    public record SentimentUpdate(int? Sentiment);
    public record ProgressUpdate(int? Season, int? Episode);
    public record SeenRequest(int TmdbId, string MediaType, string Title, string PosterPath, string AvailableOnJson, string BackdropPath = "");
}
