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

            // parallel-fetch details + trailers so the user-perceived round-trip stays
            // single. Trailers live in their own cache so curation fan-out doesn't
            // pay for the videos block it never reads. (#110)
            Task<ShowDetails?> detailsTask = tmdb.GetDetailsAsync(tmdbId, mediaType);
            Task<List<TrailerDto>> trailersTask = tmdb.GetTrailersAsync(tmdbId, mediaType);
            await Task.WhenAll(detailsTask, trailersTask);

            ShowDetails? details = detailsTask.Result;
            if (details == null) return Results.NotFound();

            // details is already a defensive copy from TmdbService — mutating it here
            // (AvailableOn, Trailers) cannot bleed back into the cache. (#109)
            details.Trailers = trailersTask.Result;

            string userId = user.GetUserId();
            List<int> providerIds = await db.GetUserServicesAsync(userId);
            HashSet<int> userProviders = [.. providerIds];

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
