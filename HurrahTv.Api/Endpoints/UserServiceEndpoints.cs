using HurrahTv.Api.Services;

namespace HurrahTv.Api.Endpoints;

public static class UserServiceEndpoints
{
    public static void MapUserServiceEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/services");

        group.MapGet("", async (DbService db) =>
        {
            var ids = await db.GetUserServicesAsync();
            return Results.Ok(ids);
        });

        group.MapPut("", async (List<int> providerIds, DbService db) =>
        {
            await db.SetUserServicesAsync(providerIds);
            return Results.Ok();
        });
    }
}
