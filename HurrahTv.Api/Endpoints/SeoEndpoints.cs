namespace HurrahTv.Api.Endpoints;

// robots.txt + sitemap.xml for #11. The single deployment is served from both the production
// custom domain (hurrah.tv) and the staging slot (staging.hurrah.tv / *.azurewebsites.net), so a
// static file can't differ per environment — robots.txt is generated per-request from the Host.
// Only hurrah.tv is indexable; staging and the raw slot hostnames must return Disallow so search
// engines don't index staging (duplicate content + leaked unreleased features) — the gate is the
// load-bearing correctness here.
public static class SeoEndpoints
{
    private const string ProductionHost = "hurrah.tv";
    private const string CanonicalBaseUrl = "https://hurrah.tv";

    public static void MapSeoEndpoints(this WebApplication app)
    {
        app.MapGet("/robots.txt", (HttpContext ctx) =>
            Results.Text(BuildRobotsTxt(ctx.Request.Host.Host), "text/plain")).AllowAnonymous();

        app.MapGet("/sitemap.xml", () =>
            Results.Text(BuildSitemapXml(), "application/xml")).AllowAnonymous();
    }

    // only the canonical custom domain is indexable — NOT www, the staging subdomain, the raw
    // *.azurewebsites.net slot hostnames, or localhost. (We don't bind www, but guard it anyway.)
    public static bool IsProductionHost(string host) =>
        string.Equals(host, ProductionHost, StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "www." + ProductionHost, StringComparison.OrdinalIgnoreCase);

    public static string BuildRobotsTxt(string host) =>
        IsProductionHost(host)
            ? $"User-agent: *\nAllow: /\nSitemap: {CanonicalBaseUrl}/sitemap.xml\n"
            : "User-agent: *\nDisallow: /\n";

    // the app is auth-gated (Home and every watchlist route are [Authorize]), so the only publicly
    // indexable surface is the canonical landing page. Listing auth-gated SPA routes would just feed
    // crawlers the WASM shell, not real content — so the sitemap is deliberately the homepage only.
    public static string BuildSitemapXml() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>{CanonicalBaseUrl}/</loc></url>
        </urlset>

        """;
}
