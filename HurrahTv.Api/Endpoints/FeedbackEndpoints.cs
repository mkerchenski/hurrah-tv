using System.Security.Claims;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Endpoints;

// in-app feedback (#19): an authenticated, rate-limited, honeypot-guarded submit endpoint that
// stores to the Feedback table, plus an admin-only list read. GetUserId() (HurrahTv.Api) is in
// scope via the parent namespace.
public static class FeedbackEndpoints
{
    public const string RateLimitPolicy = "feedback";

    private static readonly HashSet<string> AllowedCategories =
        new(StringComparer.OrdinalIgnoreCase) { "bug", "feature", "general" };

    private const int MaxMessageLength = 4000;
    private const int MaxEmailLength = 255;

    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/api/feedback", async (FeedbackSubmission submission, ClaimsPrincipal user, DbService db) =>
        {
            // honeypot: real users never fill the hidden Website field. If it's set, accept-and-drop —
            // return 200 so a bot can't tell a filtered submission from a stored one.
            if (!string.IsNullOrWhiteSpace(submission.Website))
                return Results.Ok();

            string message = (submission.Message ?? "").Trim();
            if (message.Length == 0)
                return Results.BadRequest("Message is required");
            if (message.Length > MaxMessageLength)
                message = message[..MaxMessageLength];

            // unknown/empty category falls back to "general" rather than 400 — never reject real feedback over a label
            string category = AllowedCategories.Contains(submission.Category ?? "")
                ? submission.Category!.ToLowerInvariant()
                : "general";

            string? email = string.IsNullOrWhiteSpace(submission.ContactEmail) ? null : submission.ContactEmail.Trim();
            if (email is { Length: > MaxEmailLength })
                email = email[..MaxEmailLength];

            await db.SubmitFeedbackAsync(user.GetUserId(), category, message, email);
            return Results.Ok();
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicy);

        app.MapGet("/api/admin/feedback", async (DbService db) =>
            Results.Ok(await db.GetFeedbackAsync()))
        .RequireAuthorization("Admin");
    }
}
