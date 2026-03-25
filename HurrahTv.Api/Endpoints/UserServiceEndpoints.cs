using System.Security.Claims;
using HurrahTv.Api.Services;

namespace HurrahTv.Api.Endpoints;

public static class UserServiceEndpoints
{
    public static void MapUserServiceEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/services").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.FindFirstValue("sub")!;
            var ids = await db.GetUserServicesAsync(userId);
            return Results.Ok(ids);
        });

        group.MapPut("", async (List<int> providerIds, ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.FindFirstValue("sub")!;
            await db.SetUserServicesAsync(providerIds, userId);
            return Results.Ok();
        });
    }
}
