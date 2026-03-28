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

        // trending this week, filtered to user's services, genres, and dismissals
        group.MapGet("/for-you", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.FindFirstValue("sub")!;
            DbService.UserPreferences prefs = await db.GetUserPreferencesAsync(userId);

            List<SearchResult> results = await tmdb.TrendingAsync(mediaType ?? "all", "week");
            results = await tmdb.FilterToUserServicesAsync(results, prefs.ProviderIds);
            results = ApplyPreferenceFilters(results, prefs);

            return Results.Ok(results);
        }).RequireAuthorization();

        // recently released content (over-fetches 2 pages to backfill dismissals)
        group.MapGet("/new", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.FindFirstValue("sub")!;
            string resolvedType = mediaType ?? "tv";
            DbService.UserPreferences prefs = await db.GetUserPreferencesAsync(userId);

            Task<List<SearchResult>> page1 = tmdb.NewOnServicesAsync(prefs.ProviderIds, resolvedType, prefs.GenreIds);
            Task<List<SearchResult>> page2 = tmdb.NewOnServicesAsync(prefs.ProviderIds, resolvedType, prefs.GenreIds, page: 2);
            await Task.WhenAll(page1, page2);

            List<SearchResult> results = [.. page1.Result, .. page2.Result];
            results = results.DistinctBy(r => r.TmdbId).ToList();
            results = await tmdb.FilterToUserServicesAsync(results, prefs.ProviderIds);
            results = ApplyPreferenceFilters(results, prefs);

            return Results.Ok(results.Take(20).ToList());
        }).RequireAuthorization();

    }

    private static List<SearchResult> ApplyPreferenceFilters(List<SearchResult> results, DbService.UserPreferences prefs)
    {
        if (prefs.GenreIds.Count > 0)
        {
            HashSet<int> userGenres = [.. prefs.GenreIds];
            results = results.Where(r => r.GenreIds.Any(g => userGenres.Contains(g))).ToList();
        }
        if (prefs.Dismissed.Count > 0)
            results = results.Where(r => !prefs.Dismissed.Contains(r.TmdbId)).ToList();

        return results;
    }
}
