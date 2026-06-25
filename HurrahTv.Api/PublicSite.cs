namespace HurrahTv.Api;

// the canonical public production origin, shared by the SEO endpoints (robots/sitemap indexing
// gate, #11) and the OG-preview middleware (og:url, #98) so the host can't drift between them.
public static class PublicSite
{
    public const string Host = "hurrah.tv";
    public const string Origin = "https://" + Host;

    // only the canonical custom domain is "production" — NOT www, the staging subdomain, the raw
    // *.azurewebsites.net slot hostnames, or localhost. (We don't bind www, but guard it anyway.)
    public static bool IsProductionHost(string host) =>
        string.Equals(host, Host, StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "www." + Host, StringComparison.OrdinalIgnoreCase);
}
