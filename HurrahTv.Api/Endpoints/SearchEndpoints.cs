using HurrahTv.Api.Services;

namespace HurrahTv.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/search");

        group.MapGet("", async (string q, int page, TmdbService tmdb) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest("Query required");
            var results = await tmdb.SearchAsync(q, page > 0 ? page : 1);
            return Results.Ok(results);
        });

        group.MapGet("/trending", async (string? mediaType, TmdbService tmdb) =>
        {
            var results = await tmdb.TrendingAsync(mediaType ?? "all");
            return Results.Ok(results);
        });

        group.MapGet("/provider/{providerId:int}", async (int providerId, string? mediaType, int? page, TmdbService tmdb) =>
        {
            var results = await tmdb.DiscoverByProviderAsync(providerId, mediaType ?? "tv", page ?? 1);
            return Results.Ok(results);
        });
    }
}
