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

        group.MapGet("/for-you", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
            await GetPersonalizedAsync(mediaType, user, db, tmdb, recentOnly: false)).RequireAuthorization();

        group.MapGet("/new", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
            await GetPersonalizedAsync(mediaType, user, db, tmdb, recentOnly: true)).RequireAuthorization();

    }

    private static async Task<IResult> GetPersonalizedAsync(string? mediaType, ClaimsPrincipal user,
        DbService db, TmdbService tmdb, bool recentOnly)
    {
        string resolvedType = mediaType ?? MediaTypes.Tv;
        if (!MediaTypes.IsValid(resolvedType))
            return Results.BadRequest("mediaType must be 'movie' or 'tv'");

        string userId = user.GetUserId();
        DbService.UserPreferences prefs = await db.GetUserPreferencesAsync(userId);

        List<SearchResult> results = recentOnly
            ? await tmdb.NewOnServicesAsync(prefs.ProviderIds, resolvedType, prefs.GenreIds)
            : await tmdb.PopularOnServicesAsync(prefs.ProviderIds, resolvedType, prefs.GenreIds);

        results = await tmdb.FilterToUserServicesAsync(results, prefs.ProviderIds);
        results = ApplyPreferenceFilters(results, prefs);

        return Results.Ok(results.Take(20).ToList());
    }

    private static List<SearchResult> ApplyPreferenceFilters(List<SearchResult> results, DbService.UserPreferences prefs)
    {
        if (prefs.GenreIds.Count > 0)
        {
            HashSet<int> userGenres = [.. prefs.GenreIds];
            results = [.. results.Where(r => r.GenreIds.Any(g => userGenres.Contains(g)))];
        }
        if (prefs.Dismissed.Count > 0)
            results = [.. results.Where(r => !prefs.Dismissed.Contains(r.TmdbId))];

        return results;
    }
}
