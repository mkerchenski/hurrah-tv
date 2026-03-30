using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class UserServiceEndpoints
{
    public static void MapUserServiceEndpoints(this WebApplication app)
    {
        RouteGroupBuilder services = app.MapGroup("/api/services").RequireAuthorization();

        services.MapGet("", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            List<int> ids = await db.GetUserServicesAsync(userId);
            return Results.Ok(ids);
        });

        services.MapPut("", async (List<int> providerIds, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.SetUserServicesAsync(providerIds, userId);
            return Results.Ok();
        });

        RouteGroupBuilder genres = app.MapGroup("/api/genres").RequireAuthorization();

        genres.MapGet("", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            List<int> ids = await db.GetUserGenresAsync(userId);
            return Results.Ok(ids);
        });

        genres.MapPut("", async (List<int> genreIds, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.SetUserGenresAsync(genreIds, userId);
            return Results.Ok();
        });

        RouteGroupBuilder dismissals = app.MapGroup("/api/dismissals").RequireAuthorization();

        dismissals.MapPost("/{tmdbId:int}", async (int tmdbId, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.DismissAsync(tmdbId, userId);
            return Results.Ok();
        });

        dismissals.MapDelete("", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.ClearDismissalsAsync(userId);
            return Results.Ok();
        });

        RouteGroupBuilder settings = app.MapGroup("/api/settings").RequireAuthorization();

        settings.MapGet("", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            UserSettings settings = await db.GetUserSettingsAsync(userId);
            return Results.Ok(settings);
        });

        settings.MapPut("", async (UserSettings body, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            await db.SaveUserSettingsAsync(userId, body);
            return Results.Ok();
        });
    }
}
