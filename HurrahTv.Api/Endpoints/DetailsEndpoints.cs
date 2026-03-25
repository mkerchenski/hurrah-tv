using HurrahTv.Api.Services;

namespace HurrahTv.Api.Endpoints;

public static class DetailsEndpoints
{
    public static void MapDetailsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/details/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId, TmdbService tmdb) =>
        {
            if (mediaType is not "movie" and not "tv")
                return Results.BadRequest("mediaType must be 'movie' or 'tv'");

            var details = await tmdb.GetDetailsAsync(tmdbId, mediaType);
            return details != null ? Results.Ok(details) : Results.NotFound();
        });

        app.MapGet("/api/providers/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId, TmdbService tmdb) =>
        {
            var providers = await tmdb.GetWatchProvidersAsync(tmdbId, mediaType);
            return Results.Ok(providers);
        });
    }
}
