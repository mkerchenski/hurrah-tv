using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class SentimentEndpoints
{
    public static void MapSentimentEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/shows").RequireAuthorization();

        // get all season + episode sentiments for a show
        group.MapGet("/{tmdbId:int}/sentiments", async (int tmdbId, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            ShowSentiments sentiments = await db.GetShowSentimentsAsync(tmdbId, userId);
            return Results.Ok(sentiments);
        });

        // set season sentiment (pass null to clear)
        group.MapPut("/{tmdbId:int}/seasons/{seasonNumber:int}/sentiment",
            async (int tmdbId, int seasonNumber, SentimentBody body, ClaimsPrincipal user, DbService db) =>
        {
            if (body.Sentiment is not null and (< 1 or > 3))
                return Results.BadRequest("Sentiment must be 1 (down), 2 (up), or 3 (favorite)");

            string userId = user.GetUserId();
            await db.SetSeasonSentimentAsync(tmdbId, seasonNumber, body.Sentiment, userId);
            return Results.Ok();
        });

        // set episode sentiment (pass null to clear)
        group.MapPut("/{tmdbId:int}/seasons/{seasonNumber:int}/episodes/{episodeNumber:int}/sentiment",
            async (int tmdbId, int seasonNumber, int episodeNumber, SentimentBody body, ClaimsPrincipal user, DbService db) =>
        {
            if (body.Sentiment is not null and (< 1 or > 3))
                return Results.BadRequest("Sentiment must be 1 (down), 2 (up), or 3 (favorite)");

            string userId = user.GetUserId();
            await db.SetEpisodeSentimentAsync(tmdbId, seasonNumber, episodeNumber, body.Sentiment, userId);
            return Results.Ok();
        });
    }

    public record SentimentBody(int? Sentiment);
}
