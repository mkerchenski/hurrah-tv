using HurrahTv.Api.Endpoints;

namespace HurrahTv.Api.Tests;

// pins #11 — the prod/staging robots gate. Getting this wrong means search engines index staging
// (duplicate content + leaked unreleased features), the exact mistake the issue calls out.
// Pure helpers, so no WebApplicationFactory / Postgres needed.
public class SeoEndpointsTests
{
    [Theory]
    [InlineData("hurrah.tv")]
    [InlineData("www.hurrah.tv")]
    [InlineData("HURRAH.TV")] // host comparison is case-insensitive
    public void Robots_ProductionHost_AllowsAndReferencesSitemap(string host)
    {
        string robots = SeoEndpoints.BuildRobotsTxt(host);
        Assert.Contains("Allow: /", robots);
        Assert.Contains("Sitemap: https://hurrah.tv/sitemap.xml", robots);
        Assert.DoesNotContain("Disallow: /", robots);
    }

    [Theory]
    [InlineData("staging.hurrah.tv")]
    [InlineData("hurrahtv-api-staging.azurewebsites.net")]
    [InlineData("hurrahtv-api.azurewebsites.net")] // raw prod slot hostname stays unindexed — only the custom domain is
    [InlineData("localhost")]
    public void Robots_NonProductionHost_BlocksAll(string host)
    {
        string robots = SeoEndpoints.BuildRobotsTxt(host);
        Assert.Contains("Disallow: /", robots);
        Assert.DoesNotContain("Allow: /", robots);
        Assert.DoesNotContain("Sitemap:", robots);
    }

    [Fact]
    public void Sitemap_ListsCanonicalHomepage_AndIsWellFormed()
    {
        string xml = SeoEndpoints.BuildSitemapXml();
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">", xml);
        Assert.Contains("<loc>https://hurrah.tv/</loc>", xml);
    }
}
