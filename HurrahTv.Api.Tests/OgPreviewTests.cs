using System.Net;
using System.Text;
using HurrahTv.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HurrahTv.Api.Tests;

// covers issue #98 — link-preview bots hitting /details/{tv|movie}/{id} get a
// per-show OG card; non-bot traffic and TMDb-failure paths fall through to the
// usual MapFallbackToFile pipeline so we never serve a half-rendered card.
[Collection("postgres")]
public class OgPreviewTests(PostgresFixture fx)
{
    private const string TwitterBot = "Twitterbot/1.0";
    private const string FacebookBot = "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)";
    private const string ChromeBrowser = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private const string StubTitle = "Severance";
    private const string StubOverview = "Mark leads a team of office workers whose memories have been surgically divided between their work and personal lives.";
    private const string StubBackdrop = "/lazy-backdrop.jpg";

    [Fact]
    public async Task BotUserAgent_TvDetailsPath_ReturnsOgHtml()
    {
        using HttpClient client = CreateClient(useDetailsStub: true);
        client.DefaultRequestHeaders.Add("User-Agent", TwitterBot);

        HttpResponseMessage response = await client.GetAsync("/details/tv/1234");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains($"og:title\" content=\"{StubTitle}\"", body);
        Assert.Contains($"og:image\" content=\"https://image.tmdb.org/t/p/w1280{StubBackdrop}\"", body);
        Assert.Contains("og:type\" content=\"video.tv_show\"", body);
        Assert.Contains("og:url\" content=\"https://hurrah.tv/details/tv/1234\"", body);
        Assert.Contains("twitter:card\" content=\"summary_large_image\"", body);
    }

    [Fact]
    public async Task FacebookBot_AlsoMatches()
    {
        using HttpClient client = CreateClient(useDetailsStub: true);
        client.DefaultRequestHeaders.Add("User-Agent", FacebookBot);

        HttpResponseMessage response = await client.GetAsync("/details/movie/1234");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("og:type\" content=\"video.movie\"", body);
        Assert.Contains($"og:title\" content=\"{StubTitle}\"", body);
    }

    [Fact]
    public async Task NonBotUserAgent_DoesNotShortCircuit_OgHtml()
    {
        // even with a stub that would happily return TMDb data, a real-browser UA must
        // fall through to the WASM bootstrap. The exact status downstream may be 404
        // (no wwwroot/index.html in the test build) — the assertion that matters is
        // that we did NOT render the OG card for a non-bot.
        using HttpClient client = CreateClient(useDetailsStub: true);
        client.DefaultRequestHeaders.Add("User-Agent", ChromeBrowser);

        HttpResponseMessage response = await client.GetAsync("/details/tv/1234");
        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain($"og:title\" content=\"{StubTitle}\"", body);
    }

    [Fact]
    public async Task BotUserAgent_TmdbFailure_FallsThroughGracefully()
    {
        // default fixture uses StubTmdbHandler which returns 404 → GetDetailsAsync
        // returns null → middleware must pass through, not 500.
        using HttpClient client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", TwitterBot);

        HttpResponseMessage response = await client.GetAsync("/details/tv/9999");
        string body = await response.Content.ReadAsStringAsync();

        // we may land on a 404 from MapFallbackToFile (no test wwwroot) — that's fine.
        // the contract under test: we didn't short-circuit with a half-rendered OG card.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("og:title\" content=\"", body);
    }

    [Theory]
    [InlineData("/details/tv")]              // missing id
    [InlineData("/details/tv/abc")]          // non-numeric id
    [InlineData("/details/show/123")]        // unknown media type
    [InlineData("/something/tv/123")]        // wrong root
    public async Task BotUserAgent_NonMatchingPath_DoesNotShortCircuit(string path)
    {
        using HttpClient client = CreateClient(useDetailsStub: true);
        client.DefaultRequestHeaders.Add("User-Agent", TwitterBot);

        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain($"og:title\" content=\"{StubTitle}\"", body);
    }

    private HttpClient CreateClient(bool useDetailsStub)
    {
        if (!useDetailsStub) return fx.Factory.CreateClient();

        WebApplicationFactory<Program> factory = fx.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddHttpClient<TmdbService>()
                .ConfigurePrimaryHttpMessageHandler(() => new FakeTmdbDetailsHandler(StubTitle, StubOverview, StubBackdrop))));

        return factory.CreateClient();
    }
}

internal sealed class FakeTmdbDetailsHandler(string title, string overview, string backdrop) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // TmdbService.GetDetailsAsync inspects mediaType from the URL — "tv/{id}" or
        // "movie/{id}". We return a generic shape that satisfies both branches.
        string mediaType = request.RequestUri?.AbsolutePath.Contains("/movie/") == true ? "movie" : "tv";
        string nameField = mediaType == "movie"
            ? $"\"title\": \"{title}\","
            : $"\"name\": \"{title}\",";

        string json = "{" +
            "\"id\": 1234," +
            nameField +
            $"\"overview\": \"{overview}\"," +
            $"\"backdrop_path\": \"{backdrop}\"," +
            "\"poster_path\": \"/p.jpg\"," +
            "\"tagline\": \"\"," +
            "\"vote_average\": 8.5," +
            "\"status\": \"Returning Series\"," +
            "\"genres\": []," +
            "\"seasons\": []," +
            "\"first_air_date\": \"2022-02-18\"" +
            "}";

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
