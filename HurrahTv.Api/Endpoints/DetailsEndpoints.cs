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
            if (!MediaTypes.IsValid(mediaType))
                return Results.BadRequest("mediaType must be 'movie' or 'tv'");

            ShowDetails? details = await tmdb.GetDetailsAsync(tmdbId, mediaType);
            if (details == null) return Results.NotFound();

            string userId = user.GetUserId();
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            HashSet<int> userProviders = [.. providerIds];

            // use providers already fetched by GetDetailsAsync (via append_to_response)
            List<AvailableService> allProviders = details.AvailableOn;

            List<AvailableService> streaming = [.. allProviders.Where(s => s.Type is ProviderType.Flatrate or ProviderType.Ads)];
            List<AvailableService> userMatches = [.. streaming.Where(s => userProviders.Contains(s.ProviderId))];

            if (userMatches.Count > 0)
            {
                // show only user's matching services
                details.AvailableOn = userMatches;
            }
            else if (streaming.Count > 0)
            {
                // not on user's services but streaming somewhere — show all streaming options
                details.AvailableOn = streaming;
            }
            else
            {
                // not streaming anywhere — show buy/rent so user can still find it
                details.AvailableOn = allProviders;
            }

            return Results.Ok(details);
        }).RequireAuthorization();

        app.MapGet("/api/details/tv/{tmdbId:int}/season/{seasonNumber:int}", async (int tmdbId, int seasonNumber, TmdbService tmdb) =>
        {
            SeasonDetail? season = await tmdb.GetSeasonAsync(tmdbId, seasonNumber);
            return season != null ? Results.Ok(season) : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet("/api/providers/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId, TmdbService tmdb) =>
        {
            if (!MediaTypes.IsValid(mediaType))
                return Results.BadRequest("mediaType must be 'movie' or 'tv'");

            List<AvailableService> providers = await tmdb.GetWatchProvidersAsync(tmdbId, mediaType);
            return Results.Ok(providers);
        }).RequireAuthorization();
    }
}
