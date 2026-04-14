using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class EpisodeEndpoints
{
    public static void MapEpisodeEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/episodes").RequireAuthorization();

        // mark an episode as watched (removes it from Continue Watching row)
        group.MapPost("/watched", async (WatchedEpisodeRequest req, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.MarkEpisodeWatchedAsync(userId, req.TmdbId, req.Season, req.Episode);
            return Results.NoContent();
        });

        // unmark an episode as watched (re-adds it to Continue Watching if still the latest episode)
        group.MapDelete("/watched", async (WatchedEpisodeRequest req, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.UnmarkEpisodeWatchedAsync(userId, req.TmdbId, req.Season, req.Episode);
            return Results.NoContent();
        });

        // watched episodes for a specific show — used by EpisodeBrowser on the Details page
        group.MapGet("/watched/{tmdbId:int}", async (int tmdbId, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            List<WatchedEpisode> episodes = await db.GetWatchedEpisodesForShowAsync(userId, tmdbId);
            return Results.Ok(episodes);
        });
    }
}
