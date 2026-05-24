using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using HurrahTv.Api.Services;
using HurrahTv.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HurrahTv.Api.Tests;

// pins #109 — DetailsEndpoints used to mutate the cached ShowDetails instance via
// `details.AvailableOn = userMatches`, so a second request for the same tmdbId
// would see the previous user's filtered provider list instead of the full set.
// The fix: TmdbService.GetDetailsAsync hands callers a defensive clone, so
// per-request mutation can't bleed across users.
[Collection("postgres")]
public class DetailsCacheIsolationTests(PostgresFixture fx) : IAsyncLifetime
{
    private const int NetflixId = 8;
    private const int HuluId = 15;
    private const int StubTmdbId = 1234;

    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TwoUsers_DifferentServices_EachSeesTheirOwnProvider_AcrossSequentialDetailsCalls()
    {
        // user A has only Netflix; user B has only Hulu. Both fetch the same tmdbId.
        // Without the #109 fix the second call would see the first user's filtered
        // AvailableOn (the cached instance was mutated), so user B would see no
        // matching service and fall through to the "show all streaming" branch.
        WebApplicationFactory<Program> factory = fx.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddHttpClient<TmdbService>()
                .ConfigurePrimaryHttpMessageHandler(() => new MultiProviderTmdbHandler())));

        await SeedUserServiceAsync("user-A", NetflixId);
        await SeedUserServiceAsync("user-B", HuluId);

        HttpClient userA = AuthClient(factory, "user-A");
        HttpClient userB = AuthClient(factory, "user-B");

        // first call warms the cache and filters to Netflix for user A.
        ShowDetails? aResponse = await userA.GetFromJsonAsync<ShowDetails>($"/api/details/tv/{StubTmdbId}");
        Assert.NotNull(aResponse);
        Assert.Single(aResponse!.AvailableOn);
        Assert.Equal(NetflixId, aResponse.AvailableOn[0].ProviderId);

        // second call hits the cache. The cached entry must still contain both providers
        // — if user A's filtered list leaked back into the cache, user B would see Netflix
        // (no Hulu match) and the endpoint would fall through to the "show all streaming"
        // branch, which still includes Netflix and skips Hulu entirely.
        ShowDetails? bResponse = await userB.GetFromJsonAsync<ShowDetails>($"/api/details/tv/{StubTmdbId}");
        Assert.NotNull(bResponse);
        Assert.Single(bResponse!.AvailableOn);
        Assert.Equal(HuluId, bResponse.AvailableOn[0].ProviderId);

        // and re-fetch as user A to confirm A's view is still Netflix-only (not
        // accidentally widened by user B's mutation pushing Hulu into the cache).
        ShowDetails? aAgain = await userA.GetFromJsonAsync<ShowDetails>($"/api/details/tv/{StubTmdbId}");
        Assert.NotNull(aAgain);
        Assert.Single(aAgain!.AvailableOn);
        Assert.Equal(NetflixId, aAgain.AvailableOn[0].ProviderId);
    }

    private async Task SeedUserServiceAsync(string userId, int providerId)
    {
        using NpgsqlConnection db = new(fx.ConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync(
            "INSERT INTO UserServices (UserId, ProviderId, IsActive) VALUES (@UserId, @ProviderId, TRUE)",
            new { UserId = userId, ProviderId = providerId });
    }

    private HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestAuth.IssueToken(fx, userId));
        return client;
    }
}

// returns TMDb details with Netflix + Hulu in the flatrate watch/providers block,
// and an empty videos response for the separate /videos call introduced by #110.
internal sealed class MultiProviderTmdbHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        string body = path.EndsWith("/videos") ? VideosBody : DetailsBody;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    private const string VideosBody = "{\"results\":[]}";

    private const string DetailsBody = """
        {
            "id": 1234,
            "name": "Stub Show",
            "overview": "test",
            "backdrop_path": "/b.jpg",
            "poster_path": "/p.jpg",
            "tagline": "",
            "vote_average": 8.0,
            "status": "Returning Series",
            "genres": [],
            "seasons": [],
            "first_air_date": "2022-01-01",
            "watch/providers": {
                "results": {
                    "US": {
                        "flatrate": [
                            { "provider_id": 8,  "provider_name": "Netflix", "logo_path": "/n.png" },
                            { "provider_id": 15, "provider_name": "Hulu",    "logo_path": "/h.png" }
                        ]
                    }
                }
            }
        }
        """;
}
