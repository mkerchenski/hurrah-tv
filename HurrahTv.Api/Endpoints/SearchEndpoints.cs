using System.Security.Claims;
using System.Text.RegularExpressions;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static partial class SearchEndpoints
{
    private const int MaxResultsToEnrich = 30;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static void MapSearchEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/search");

        group.MapGet("", async (string? q, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            string query = NormalizeQuery(q ?? "");
            if (query.Length < 2) return Results.BadRequest("Query too short");
            if (query.Length > 200) return Results.BadRequest("Query too long");
            List<SearchResult> results = await tmdb.SearchAsync(query);
            bool hadResults = results.Count > 0;

            string userId = user.GetUserId();
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            results = await tmdb.FilterToUserServicesAsync([.. results.Take(MaxResultsToEnrich)], providerIds);

            return Results.Ok(new SearchResponse { Results = results, HasResultsOnOtherServices = hadResults && results.Count == 0 });
        }).RequireAuthorization();

        group.MapGet("/for-you", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
            await GetPersonalizedAsync(mediaType, user, db, tmdb, recentOnly: false)).RequireAuthorization();

        group.MapGet("/new", async (string? mediaType, ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
            await GetPersonalizedAsync(mediaType, user, db, tmdb, recentOnly: true)).RequireAuthorization();

        // recommendations based on a specific title, filtered to user's services
        group.MapGet("/recommendations/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId,
            ClaimsPrincipal user, DbService db, TmdbService tmdb) =>
        {
            if (!MediaTypes.IsValid(mediaType)) return Results.BadRequest("Invalid media type");

            string userId = user.GetUserId();
            DbService.UserPreferences prefs = await db.GetUserPreferencesAsync(userId);

            List<SearchResult> recs = await tmdb.GetRecommendationsAsync(tmdbId, mediaType);
            recs = await tmdb.FilterToUserServicesAsync(recs, prefs.ProviderIds);
            recs = ApplyPreferenceFilters(recs, prefs);
            recs = BoostRecent(recs);

            return Results.Ok(recs.Take(20).ToList());
        }).RequireAuthorization();

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

        // boost recent shows to the front for trending/popular (not for "new" which is already date-filtered)
        if (!recentOnly)
            results = BoostRecent(results);

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

    // sort results with recency boost: shows from the last 2 years float to the front
    // (sorted newest-first), then older shows follow in their original order
    private static List<SearchResult> BoostRecent(List<SearchResult> results)
    {
        string cutoff = DateTime.UtcNow.AddYears(-2).ToString("yyyy-MM-dd");

        List<SearchResult> recent = [.. results.Where(r => (r.DisplayDate ?? "") .CompareTo(cutoff) >= 0)
            .OrderByDescending(r => r.DisplayDate)];
        List<SearchResult> older = [.. results.Where(r => (r.DisplayDate ?? "").CompareTo(cutoff) < 0)];

        return [.. recent, .. older];
    }

    private static string NormalizeQuery(string query)
    {
        string normalized = query
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace('\u2014', '-').Replace('\u2013', '-');

        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }
}
