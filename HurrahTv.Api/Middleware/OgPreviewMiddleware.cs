using System.Net;
using System.Text;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Middleware;

// SSR path for link-preview bots requesting /details/{tv|movie}/{id}. Real-user
// (non-bot) traffic falls through untouched and lands on the Blazor WASM bootstrap
// served by MapFallbackToFile, as before. See issue #98.
//
// The detection deliberately stays Contains-any over a small list: the bots that
// matter (Twitterbot, facebookexternalhit-on-iMessage, Applebot, Slack, Discord,
// etc.) advertise themselves consistently and the list doesn't churn. We don't
// try to fight a real attacker — the surface is "give crawlers a nicer card",
// not authentication.
public sealed class OgPreviewMiddleware(RequestDelegate next)
{
    // canonical production origin — used in og:url even when the request hit
    // staging.hurrah.tv, so a shared link from a staging session still resolves
    // to the prod card on the next crawl
    private const string PublicBaseUrl = "https://hurrah.tv";

    private static readonly string[] BotUaMarkers =
    [
        "Twitterbot",
        "facebookexternalhit",
        "Applebot",
        "LinkedInBot",
        "Slackbot",
        "WhatsApp",
        "Discordbot",
        "TelegramBot",
        "Pinterest",
    ];

    public async Task InvokeAsync(HttpContext ctx, TmdbService tmdb, ILogger<OgPreviewMiddleware> log)
    {
        if (!IsBot(ctx.Request.Headers.UserAgent.ToString()) ||
            !TryParseDetailsPath(ctx.Request.Path, out string mediaType, out int tmdbId))
        {
            await next(ctx);
            return;
        }

        ShowDetails? details = null;
        try { details = await tmdb.GetDetailsAsync(tmdbId, mediaType); }
        catch (Exception ex) { log.LogWarning(ex, "OgPreview TMDb fetch failed for {Media}/{Id}", mediaType, tmdbId); }

        if (details is null)
        {
            // graceful fallback: let MapFallbackToFile serve the site-wide HTML so the
            // crawler at least gets the generic card instead of a 500
            await next(ctx);
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "public, max-age=21600"; // 6h, matches TmdbService cache
        await ctx.Response.WriteAsync(RenderOgHtml(details));
    }

    internal static bool IsBot(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return false;
        foreach (string marker in BotUaMarkers)
        {
            if (userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool TryParseDetailsPath(PathString path, out string mediaType, out int tmdbId)
    {
        mediaType = "";
        tmdbId = 0;
        if (!path.HasValue) return false;

        // expected shape: /details/{tv|movie}/{int}
        string[] parts = path.Value!.Trim('/').Split('/');
        if (parts.Length != 3 || !parts[0].Equals("details", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[1] != "tv" && parts[1] != "movie") return false;
        if (!int.TryParse(parts[2], out int id) || id <= 0) return false;

        mediaType = parts[1];
        tmdbId = id;
        return true;
    }

    internal static string RenderOgHtml(ShowDetails details)
    {
        string title = HtmlEncode(details.Title);
        string description = HtmlEncode(Truncate(details.Overview, 200));
        string url = $"{PublicBaseUrl}/details/{details.MediaType}/{details.TmdbId}";
        string ogType = details.MediaType == "tv" ? "video.tv_show" : "video.movie";

        // prefer backdrop (1280×720, matches summary_large_image card aspect) and fall
        // back to poster if a show has no backdrop. Final fallback is the site-wide
        // icon so we never emit an empty og:image.
        string image = !string.IsNullOrEmpty(details.BackdropPath)
            ? $"https://image.tmdb.org/t/p/w1280{details.BackdropPath}"
            : !string.IsNullOrEmpty(details.PosterPath)
                ? $"https://image.tmdb.org/t/p/w500{details.PosterPath}"
                : $"{PublicBaseUrl}/icon-512.png";

        StringBuilder sb = new(2048);
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.Append("<title>").Append(title).Append(" - Hurrah.tv</title>");
        sb.Append("<meta name=\"description\" content=\"").Append(description).Append("\" />");
        sb.Append("<meta property=\"og:type\" content=\"").Append(ogType).Append("\" />");
        sb.Append("<meta property=\"og:site_name\" content=\"Hurrah.tv\" />");
        sb.Append("<meta property=\"og:title\" content=\"").Append(title).Append("\" />");
        sb.Append("<meta property=\"og:description\" content=\"").Append(description).Append("\" />");
        sb.Append("<meta property=\"og:url\" content=\"").Append(url).Append("\" />");
        sb.Append("<meta property=\"og:image\" content=\"").Append(image).Append("\" />");
        sb.Append("<meta name=\"twitter:card\" content=\"summary_large_image\" />");
        sb.Append("<meta name=\"twitter:title\" content=\"").Append(title).Append("\" />");
        sb.Append("<meta name=\"twitter:description\" content=\"").Append(description).Append("\" />");
        sb.Append("<meta name=\"twitter:image\" content=\"").Append(image).Append("\" />");
        sb.Append("<link rel=\"canonical\" href=\"").Append(url).Append("\" />");
        sb.Append("</head><body><h1>").Append(title).Append("</h1>");
        sb.Append("<p>").Append(description).Append("</p>");
        sb.Append("<p><a href=\"").Append(url).Append("\">Open on Hurrah.tv</a></p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string HtmlEncode(string? input) => WebUtility.HtmlEncode(input ?? string.Empty);

    // keep `og:description` short. Cuts on the last word boundary inside the limit so
    // "...waiting for an" doesn't get sliced as "...waiting for a"
    private static string Truncate(string input, int max)
    {
        if (string.IsNullOrEmpty(input) || input.Length <= max) return input ?? string.Empty;
        int cut = input.LastIndexOf(' ', max - 1);
        if (cut <= 0) cut = max;
        return input[..cut].TrimEnd() + "…";
    }
}
