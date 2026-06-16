using System.Globalization;
using System.Text.Json;
using HurrahTv.Api.Telemetry;
using Microsoft.ApplicationInsights;

namespace HurrahTv.Api.Endpoints;

// receives the client RUM beacon (#201) and forwards it to App Insights as a custom event so a
// captured slow load (#200) carries its phase breakdown. Anonymous, per-IP rate-limited, and
// payload-capped at the stream level (a Content-Length check alone is bypassable — see below).
public static class TelemetryEndpoints
{
    public const string RateLimitPolicy = "telemetry";
    private const long MaxPayloadBytes = 4096;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/telemetry", async (HttpContext ctx) =>
        {
            // enforce the cap at the stream level: navigator.sendBeacon sends a chunked body with
            // no Content-Length, so a header check alone (`ContentLength is > Max`) is bypassable —
            // it's null on the real browser path and the comparison silently passes. Read at most
            // MaxPayloadBytes + 1 bytes so an oversized body is rejected without ever buffering it whole.
            byte[] buffer = new byte[MaxPayloadBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(total));
                if (read == 0)
                    break;
                total += read;
            }
            if (total > MaxPayloadBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            RumSample? sample;
            try
            {
                sample = JsonSerializer.Deserialize<RumSample>(buffer.AsSpan(0, total), WebJson);
            }
            catch (JsonException)
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
                ["bootMs"] = Ms(sample.BootMs),
                ["bundleMs"] = Ms(sample.BundleMs)
            });

            return Results.Accepted();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicy);
    }

    // client IP for rate-limit partitioning. X-Forwarded-For is `client, proxy1, …` where the LAST
    // entry is the one the trusted hop (Azure App Service — a single hop here) appended, i.e. the
    // real client as Azure saw it. Taking the FIRST entry would trust whatever the client injected,
    // letting it rotate spoofed IPs to evade the per-IP limit. Falls back to the socket address when
    // the header is absent (dev/tests).
    public static string ResolveClientIp(HttpContext ctx)
    {
        string? forwarded = ctx.Request.Headers["X-Forwarded-For"].LastOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
            return forwarded.Split(',')[^1].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

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
    double BootMs,
    double BundleMs);
