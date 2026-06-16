using System.Globalization;
using HurrahTv.Api.Telemetry;
using Microsoft.ApplicationInsights;

namespace HurrahTv.Api.Endpoints;

// receives the client RUM beacon (#201) and forwards it to App Insights as a custom event so a
// captured slow load (#200) carries its phase breakdown. Anonymous, per-IP rate-limited, and
// payload-capped — the size check runs before the body is read by binding manually off HttpContext.
public static class TelemetryEndpoints
{
    public const string RateLimitPolicy = "telemetry";
    private const long MaxPayloadBytes = 4096;

    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/telemetry", async (HttpContext ctx) =>
        {
            if (ctx.Request.ContentLength is > MaxPayloadBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            RumSample? sample;
            try
            {
                sample = await ctx.Request.ReadFromJsonAsync<RumSample>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest();
            }
            if (sample is null)
                return Results.BadRequest();

            // forward only when App Insights is configured — TelemetryClient is registered by
            // AddApplicationInsightsTelemetry, which is gated on a real connection string. When
            // unconfigured (dev/local/tests) we accept-and-drop so the beacon still returns cleanly.
            // SDK 3.x EventTelemetry has no Metrics dictionary, so the timing phases go in as
            // custom dimensions (query in App Insights with `extend totalMs = toint(customDimensions.totalMs)`)
            TelemetryClient? client = ctx.RequestServices.GetService<TelemetryClient>();
            client?.TrackEvent("RumLoad", new Dictionary<string, string>
            {
                ["env"] = Clamp(sample.Env, 16),
                // page URL could in theory carry PII in its query — reuse the span scrubber
                ["url"] = PiiRedactionProcessor.Redact(Clamp(sample.Url, 512)),
                ["slow"] = sample.Slow ? "true" : "false",
                ["totalMs"] = Ms(sample.TotalMs),
                ["ttfbMs"] = Ms(sample.TtfbMs),
                ["serverMs"] = Ms(sample.ServerMs),
                ["downloadMs"] = Ms(sample.DownloadMs),
                ["domMs"] = Ms(sample.DomMs),
                ["bundleMs"] = Ms(sample.BundleMs)
            });

            return Results.Accepted();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicy);
    }

    // client IP for rate-limit partitioning — prefer the forwarded client over the proxy hop
    // (Azure App Service fronts the request), falling back to the socket address.
    public static string ResolveClientIp(HttpContext ctx) =>
        ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string Clamp(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length <= maxLen ? value : value[..maxLen];
    }

    // invariant culture so a server locale can't emit decimal commas into the dimension
    private static string Ms(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}

// RUM beacon payload (JS → Api). Api-local on purpose: rum.js posts it and nothing on the Blazor
// side consumes it, so it doesn't belong in HurrahTv.Shared.
public record RumSample(
    string? Env,
    string? Url,
    bool Slow,
    double TotalMs,
    double TtfbMs,
    double ServerMs,
    double DownloadMs,
    double DomMs,
    double BundleMs);
