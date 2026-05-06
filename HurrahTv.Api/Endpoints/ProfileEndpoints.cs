using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/profile").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, DbService db) =>
        {
            string userId = user.GetUserId();
            string? phone = user.GetPhone();
            string? firstName = await db.GetUserFirstNameAsync(userId);
            return Results.Ok(new UserProfile(phone ?? "", firstName));
        });

        // returns a fresh JWT carrying the updated firstname claim so the client
        // can update its greeting without forcing a sign-out/sign-in
        group.MapPut("", async (UpdateProfileRequest request, ClaimsPrincipal user, DbService db, AuthService auth) =>
        {
            string userId = user.GetUserId();
            string? phone = user.GetPhone();
            if (phone is null) return Results.Unauthorized();

            string? trimmed = request.FirstName?.Trim();
            if (trimmed is { Length: > 50 })
                return Results.BadRequest(new { error = "First name must be 50 characters or fewer." });
            if (trimmed is { Length: 0 }) trimmed = null;

            await db.SetUserFirstNameAsync(userId, trimmed);
            bool isAdmin = await db.IsUserAdminAsync(userId);
            string token = auth.IssueToken(userId, phone, isAdmin, trimmed);

            return Results.Ok(new AuthResponse(token, phone));
        });
    }
}
