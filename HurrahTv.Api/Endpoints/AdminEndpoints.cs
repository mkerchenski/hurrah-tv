using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        group.MapGet("/users", async (DbService db) =>
        {
            List<AdminUserSummary> users = await db.GetAdminUserSummariesAsync();
            return Results.Ok(new AdminUsersResponse(users.Count, users));
        });

        group.MapGet("/users/{userId}", async (string userId, DbService db) =>
        {
            AdminUserDetail? detail = await db.GetAdminUserDetailAsync(userId);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPost("/users/{userId}/admin", async (
            string userId,
            AdminSetAdminRequest request,
            ClaimsPrincipal caller,
            DbService db) =>
        {
            string callerId = caller.GetUserId();

            // last-admin lockout: don't let the only admin demote themselves (or anyone if they're the last)
            if (!request.IsAdmin)
            {
                int adminCount = await db.GetAdminCountAsync();
                if (adminCount <= 1)
                    return Results.BadRequest(new { error = "Cannot demote the last remaining admin." });
            }

            // self-demotion is disallowed even with other admins present — guard against accidental clicks
            if (!request.IsAdmin && userId == callerId)
                return Results.BadRequest(new { error = "Admins cannot demote themselves. Ask another admin." });

            bool ok = await db.SetUserAdminAsync(userId, request.IsAdmin);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/users/{userId}", async (
            string userId,
            ClaimsPrincipal caller,
            DbService db,
            ILogger<DbService> logger) =>
        {
            string callerId = caller.GetUserId();

            if (userId == callerId)
                return Results.BadRequest(new { error = "Admins cannot delete themselves." });

            // require explicit demotion before delete — surfaces the high-trust account
            // and prevents accidental destruction of another admin's data
            if (await db.IsUserAdminAsync(userId))
                return Results.BadRequest(new { error = "Revoke admin before deleting this user." });

            bool deleted = await db.DeleteUserAsync(userId);
            if (!deleted) return Results.NotFound();

            logger.LogWarning("Admin {CallerId} hard-deleted user {UserId}", callerId, userId);
            return Results.NoContent();
        });

        group.MapPut("/users/{userId}/firstname", async (
            string userId,
            AdminSetFirstNameRequest request,
            DbService db) =>
        {
            string? trimmed = request.FirstName?.Trim();
            if (trimmed is { Length: > 50 })
                return Results.BadRequest(new { error = "First name must be 50 characters or fewer." });
            if (trimmed is { Length: 0 }) trimmed = null;

            await db.SetUserFirstNameAsync(userId, trimmed);
            return Results.NoContent();
        });

        group.MapGet("/ai-usage", async (DbService db) =>
            Results.Ok(await db.GetAdminAiUsageAsync()));

        group.MapGet("/onboarding", async (DbService db) =>
            Results.Ok(await db.GetAdminOnboardingFunnelAsync()));
    }
}
