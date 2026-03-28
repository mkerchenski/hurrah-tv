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

        // what's trending this week, filtered to user's services
        group.MapGet("/for-you", async (ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.FindFirstValue("sub")!;
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            // use TMDb's real trending endpoint (tracks actual search/watch activity)
            List<SearchResult> results = await tmdb.TrendingAsync("all", "week");
            // filter to only content on user's services (also enriches AvailableOn)
            results = await tmdb.FilterToUserServicesAsync(results, providerIds);
            return Results.Ok(results);
        }).RequireAuthorization();

        // recently released content on user's services
        group.MapGet("/new", async (ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.FindFirstValue("sub")!;
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            List<SearchResult> results = await tmdb.NewOnServicesAsync(providerIds);
            results = await tmdb.FilterToUserServicesAsync(results, providerIds);
            return Results.Ok(results);
        }).RequireAuthorization();

    }
}
