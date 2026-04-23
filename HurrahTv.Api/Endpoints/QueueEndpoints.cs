using System.Security.Claims;
using System.Text.Json;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class QueueEndpoints
{
    private static readonly TimeSpan EpisodeCheckStaleAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan ProviderCheckStaleAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleRefreshTimeout = TimeSpan.FromSeconds(3);

    public static void MapQueueEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/queue").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, DbService db, TmdbService tmdb, ILogger<DbService> logger) =>
        {
            string userId = user.GetUserId();

            // start watched episodes + active services fetches — run in parallel with queue read and stale refresh
            Task<List<WatchedEpisode>> watchedTask = db.GetWatchedEpisodesAsync(userId);
            Task<List<int>> activeServicesTask = db.GetUserServicesAsync(userId);

            List<QueueItem> items = await db.GetQueueAsync(userId);

            List<QueueItem> staleEpisodes = [.. items
                .Where(i => i.MediaType == MediaTypes.Tv
                    && i.Status is QueueStatus.Watching or QueueStatus.WantToWatch
                    && (i.LastEpisodeCheckAt == null
                        || DateTime.UtcNow - i.LastEpisodeCheckAt > EpisodeCheckStaleAfter
                        || i.LatestEpisodeSeason == null))];

            // refresh provider data too — hides items no longer on user's active services, un-hides ones that returned
            List<QueueItem> staleProviders = [.. items
                .Where(i => i.AvailableOnCheckedAt == null
                    || DateTime.UtcNow - i.AvailableOnCheckedAt > ProviderCheckStaleAfter)];

            if (staleEpisodes.Count > 0 || staleProviders.Count > 0)
            {
                using CancellationTokenSource cts = new(StaleRefreshTimeout);
                try
                {
                    Task episodeRefresh = Task.WhenAll(staleEpisodes.Take(10).Select(async item =>
                    {
                        try
                        {
                            (DateTime? lastAired, int? lastSeason, int? lastEp, DateTime? nextAir, int? nextSeason, int? nextEp)
                                = await tmdb.GetEpisodeDatesAsync(item.TmdbId, cts.Token);
                            await db.UpdateEpisodeDatesAsync(item.Id, lastAired, lastSeason, lastEp, nextAir, nextSeason, nextEp, cts.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Failed to refresh episode dates for {TmdbId}", item.TmdbId);
                        }
                    }));

                    Task providerRefresh = Task.WhenAll(staleProviders.Take(10).Select(async item =>
                    {
                        try
                        {
                            List<AvailableService> providers = await tmdb.GetWatchProvidersAsync(item.TmdbId, item.MediaType, cts.Token);
                            string json = JsonSerializer.Serialize(providers
                                .Where(p => p.Type is ProviderType.Flatrate or ProviderType.Ads)
                                .Select(p => p.ProviderId)
                                .Distinct());
                            await db.UpdateProvidersAsync(item.Id, json, cts.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Failed to refresh providers for {TmdbId}", item.TmdbId);
                        }
                    }));

                    await Task.WhenAll(episodeRefresh, providerRefresh);

                    items = await db.GetQueueAsync(userId);
                }
                catch (OperationCanceledException)
                {
                    // timeout — return stale data
                }
            }

            // hide items not watchable on any active service. the stale-refresh above keeps
            // AvailableOnJson fresh on a 24h TTL; items beyond that window may be briefly
            // mis-hidden until the next queue load picks them up.
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
            bool updated = await db.UpdateStatusAsync(id, update.Status, userId);
            return updated ? Results.Ok() : Results.NotFound();
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
            bool updated = await db.UpdateSentimentAsync(id, update.Sentiment, userId);
            return updated ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{id:int}/progress", async (int id, ProgressUpdate update, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            bool updated = await db.UpdateProgressAsync(id, update.Season, update.Episode, userId);
            return updated ? Results.Ok() : Results.NotFound();
        });

        // "I've seen this" — adds as Finished or updates existing to Finished
        group.MapPost("/seen", async (SeenRequest request, ClaimsPrincipal user, DbService db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || !MediaTypes.IsValid(request.MediaType) || request.TmdbId <= 0)
                return Results.BadRequest("Invalid request");

            string userId = user.GetUserId();
            QueueItem? item = await db.MarkAsSeenAsync(
                request.TmdbId, request.MediaType, request.Title,
                request.PosterPath, request.AvailableOnJson, userId);
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
                request.PosterPath, request.AvailableOnJson, userId);
            return item != null ? Results.Ok(item) : Results.Problem("Ensure failed");
        });

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
    public record SeenRequest(int TmdbId, string MediaType, string Title, string PosterPath, string AvailableOnJson);
}
