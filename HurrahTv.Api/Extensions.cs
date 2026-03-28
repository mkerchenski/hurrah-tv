using System.Security.Claims;

namespace HurrahTv.Api;

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException("Missing sub claim");
}
