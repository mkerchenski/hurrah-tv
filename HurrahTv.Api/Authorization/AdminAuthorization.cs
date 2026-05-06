using HurrahTv.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace HurrahTv.Api.Authorization;

public class AdminRequirement : IAuthorizationRequirement;

public class AdminRequirementHandler(DbService db) : AuthorizationHandler<AdminRequirement>
{
    private readonly DbService _db = db;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        string? userId = context.User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId)) return;

        // re-check DB on every request — demotions take effect immediately without waiting for JWT expiry
        if (await _db.IsUserAdminAsync(userId))
            context.Succeed(requirement);
    }
}
