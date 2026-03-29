using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class QueueEndpoints
{
    private static readonly TimeSpan EpisodeCheckStaleAfter = TimeSpan.FromHours(12);

    public static void MapQueueEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/queue").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.GetUserId();
            List<QueueItem> items = await db.GetQueueAsync(userId);

            // refresh stale episode dates for TV items in background (fire-and-forget for this request)
            List<QueueItem> stale = items
                .Where(i => i.MediaType == MediaTypes.Tv
                    && i.Status is QueueStatus.Watching or QueueStatus.WantToWatch
                    && (i.LastEpisodeCheckAt == null || DateTime.UtcNow - i.LastEpisodeCheckAt > EpisodeCheckStaleAfter))
                .ToList();

            if (stale.Count > 0)
            {
                // refresh up to 10 items per request to avoid TMDb rate limits
                _ = Task.Run(async () =>
                {
                    foreach (QueueItem item in stale.Take(10))
                    {
                        try
                        {
                            (DateTime? lastAired, DateTime? nextAir) = await tmdb.GetEpisodeDatesAsync(item.TmdbId);
                            await db.UpdateEpisodeDatesAsync(item.Id, lastAired, nextAir);
                        }
                        catch { /* swallow — non-critical background work */ }
                    }
                });
            }

            return Results.Ok(items);
        });

        group.MapPost("", async (QueueItem item, ClaimsPrincipal user, DbService db) =>
        {
            if (string.IsNullOrWhiteSpace(item.Title) || !MediaTypes.IsValid(item.MediaType) || item.TmdbId <= 0)
                return Results.BadRequest("Invalid queue item");

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

        group.MapPut("/{id:int}/rating", async (int id, RatingUpdate update, ClaimsPrincipal user, DbService db) =>
        {
            if (update.Rating is < 1 or > 5)
                return Results.BadRequest("Rating must be 1-5");

            string userId = user.GetUserId();
            bool updated = await db.UpdateRatingAsync(id, update.Rating, userId);
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

        // "I loved this" — adds as Liked or updates existing to Liked
        group.MapPost("/liked", async (SeenRequest request, ClaimsPrincipal user, DbService db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || !MediaTypes.IsValid(request.MediaType) || request.TmdbId <= 0)
                return Results.BadRequest("Invalid request");

            string userId = user.GetUserId();
            QueueItem? item = await db.MarkAsLikedAsync(
                request.TmdbId, request.MediaType, request.Title,
                request.PosterPath, request.AvailableOnJson, userId);
            return Results.Ok(item);
        });
    }

    public record QueueStatusUpdate(QueueStatus Status);
    public record PositionUpdate(int Position);
    public record RatingUpdate(int? Rating);
    public record ProgressUpdate(int? Season, int? Episode);
    public record SeenRequest(int TmdbId, string MediaType, string Title, string PosterPath, string AvailableOnJson);
}
