using System.Net;
using System.Net.Http.Headers;

namespace HurrahTv.Api.Tests;

// uses /api/queue as the canary protected endpoint — its [Authorize] gate is
// the same one every other endpoint group inherits, so a 401 here is a 401
// everywhere.
[Collection("postgres")]
public class JwtAuthTests(PostgresFixture fx)
{
    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        HttpClient client = fx.Factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/queue");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithExpiredToken_Returns401()
    {
        HttpClient client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.IssueExpiredToken(fx, "user-expired"));

        HttpResponseMessage response = await client.GetAsync("/api/queue");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_Returns200()
    {
        await fx.ResetAsync();
        HttpClient client = TestAuth.CreateClient(fx, "user-valid");

        HttpResponseMessage response = await client.GetAsync("/api/queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
