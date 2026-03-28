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

            string userId = user.GetUserId();
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            results = await tmdb.FilterToUserServicesAsync(results, providerIds);

            return Results.Ok(results);
        }).RequireAuthorization();

        // trending this week, filtered to user's services, genres, and dismissals
        group.MapGet("/for-you", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.GetUserId();
            DbService.UserPreferences prefs = await db.GetUserPreferencesAsync(userId);

            List<SearchResult> results = await tmdb.TrendingAsync(mediaType ?? MediaType.All, "week");
            results = await tmdb.FilterToUserServicesAsync(results, prefs.ProviderIds);
            results = ApplyPreferenceFilters(results, prefs);

            return Results.Ok(results);
        }).RequireAuthorization();

        group.MapGet("/new", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string userId = user.GetUserId();
            string resolvedType = mediaType ?? MediaType.Tv;
            if (!MediaType.IsValid(resolvedType))
                return Results.BadRequest("mediaType must be 'movie' or 'tv'");

            DbService.UserPreferences prefs = await db.GetUserPreferencesAsync(userId);

            List<SearchResult> results = await tmdb.NewOnServicesAsync(prefs.ProviderIds, resolvedType, prefs.GenreIds);
            results = await tmdb.FilterToUserServicesAsync(results, prefs.ProviderIds);
            results = ApplyPreferenceFilters(results, prefs);

            return Results.Ok(results);
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
