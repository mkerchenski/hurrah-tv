using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/search");

        group.MapGet("", async (string q, int page, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest("Query required");
            List<SearchResult> results = await tmdb.SearchAsync(q, page > 0 ? page : 1);

            string userId = user.FindFirstValue("sub")!;
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            results = await tmdb.FilterToUserServicesAsync(results, providerIds);

            return Results.Ok(results);
        }).RequireAuthorization();

        group.MapGet("/trending", async (string? mediaType, TmdbService tmdb) =>
        {
            var results = await tmdb.TrendingAsync(mediaType ?? "all");
            return Results.Ok(results);
        });

        // trending filtered to user's subscribed services (flatrate only)
        group.MapGet("/for-you", async (ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.FindFirstValue("sub")!;
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            var results = await tmdb.TrendingForServicesAsync(providerIds);
            return Results.Ok(results);
        }).RequireAuthorization();

        group.MapGet("/provider/{providerId:int}", async (int providerId, string? mediaType, int? page, TmdbService tmdb) =>
        {
            var results = await tmdb.DiscoverByProviderAsync(providerId, mediaType ?? "tv", page ?? 1);
            return Results.Ok(results);
        });
    }
}
