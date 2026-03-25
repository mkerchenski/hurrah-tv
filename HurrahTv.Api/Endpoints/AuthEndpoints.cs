using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth").AllowAnonymous();

        group.MapPost("/send-code", async (SendCodeRequest request, AuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber))
                return Results.BadRequest("Phone number is required");

            await auth.SendCodeAsync(request.PhoneNumber);
            return Results.Ok(new { sent = true });
        });

        group.MapPost("/verify", async (VerifyCodeRequest request, AuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.PhoneNumber) || string.IsNullOrWhiteSpace(request.Code))
                return Results.BadRequest("Phone number and code are required");

            string? token = await auth.VerifyCodeAsync(request.PhoneNumber, request.Code);
            if (token == null)
                return Results.Unauthorized();

            return Results.Ok(new AuthResponse(token, request.PhoneNumber));
        });
    }
}
