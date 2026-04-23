using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class QueueEndpoints
{
    private static readonly TimeSpan EpisodeCheckStaleAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan EpisodeRefreshTimeout = TimeSpan.FromSeconds(3);

    public static void MapQueueEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/queue").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, DbService db, TmdbService tmdb, ILogger<DbService> logger) =>
        {
            string userId = user.GetUserId();
            List<QueueItem> items = await db.GetQueueAsync(userId);

            // start watched episodes fetch — runs in parallel with any TMDb stale refresh below
            Task<List<WatchedEpisode>> watchedTask = db.GetWatchedEpisodesAsync(userId);

            List<QueueItem> stale = [.. items
                .Where(i => i.MediaType == MediaTypes.Tv
                    && i.Status is QueueStatus.Watching or QueueStatus.WantToWatch
                    && (i.LastEpisodeCheckAt == null
                        || DateTime.UtcNow - i.LastEpisodeCheckAt > EpisodeCheckStaleAfter
                        || i.LatestEpisodeSeason == null))];

            if (stale.Count > 0)
            {
                using CancellationTokenSource cts = new(EpisodeRefreshTimeout);
                try
                {
                    await Task.WhenAll(stale.Take(10).Select(async item =>
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

                    items = await db.GetQueueAsync(userId);
                }
                catch (OperationCanceledException)
                {
                    // timeout — return stale data
                }
            }

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

    public record QueueStatusUpdate(QueueStatus Status);
    public record PositionUpdate(int Position);
    public record SentimentUpdate(int? Sentiment);
    public record ProgressUpdate(int? Season, int? Episode);
    public record SeenRequest(int TmdbId, string MediaType, string Title, string PosterPath, string AvailableOnJson);
}
