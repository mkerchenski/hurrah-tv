using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class DetailsEndpoints
{
    public static void MapDetailsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/details/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId,
            ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            if (mediaType is not "movie" and not "tv")
                return Results.BadRequest("mediaType must be 'movie' or 'tv'");

            ShowDetails? details = await tmdb.GetDetailsAsync(tmdbId, mediaType);
            if (details == null) return Results.NotFound();

            // filter providers to only the user's subscribed services
            string userId = user.FindFirstValue("sub")!;
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            HashSet<int> userProviders = [.. providerIds];
            details.AvailableOn = details.AvailableOn
                .Where(s => s.Type == "flatrate" && userProviders.Contains(s.ProviderId))
                .ToList();

            return Results.Ok(details);
        }).RequireAuthorization();

        app.MapGet("/api/providers/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId, TmdbService tmdb) =>
        {
            var providers = await tmdb.GetWatchProvidersAsync(tmdbId, mediaType);
            return Results.Ok(providers);
        });
    }
}
