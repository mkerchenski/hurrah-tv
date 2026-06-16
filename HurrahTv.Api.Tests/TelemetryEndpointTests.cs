using System.Net;
using System.Text;

namespace HurrahTv.Api.Tests;

// covers the RUM telemetry intake (#201/#200): accepts a beacon, caps payload size, and
// rate-limits per IP. App Insights is unconfigured in tests, so the endpoint accepts-and-drops
// (no TelemetryClient) — these assert the HTTP contract, not the forward. Each test uses a
// distinct X-Forwarded-For so the per-IP limiter partitions don't bleed across tests.
[Collection("postgres")]
public class TelemetryEndpointTests(PostgresFixture fx)
{
    private const string ValidSample =
        "{\"env\":\"prod\",\"url\":\"/queue\",\"slow\":true,\"totalMs\":12000,\"ttfbMs\":80," +
        "\"serverMs\":40,\"downloadMs\":300,\"domMs\":150,\"bundleMs\":9000}";

    private static HttpRequestMessage Beacon(string clientIp, string json)
    {
        HttpRequestMessage req = new(HttpMethod.Post, "/api/telemetry")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Forwarded-For", clientIp);
        return req;
    }

    [Fact]
    public async Task Valid_Beacon_Returns_202()
    {
        using HttpClient client = fx.Factory.CreateClient();
        HttpResponseMessage res = await client.SendAsync(Beacon("10.0.0.1", ValidSample));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Oversized_Payload_Returns_413()
    {
        using HttpClient client = fx.Factory.CreateClient();
        string big = "{\"env\":\"prod\",\"url\":\"" + new string('x', 5000) + "\"}";
        HttpResponseMessage res = await client.SendAsync(Beacon("10.0.0.2", big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Garbage_Body_Returns_400()
    {
        using HttpClient client = fx.Factory.CreateClient();
        HttpResponseMessage res = await client.SendAsync(Beacon("10.0.0.4", "not json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Exceeding_Per_Ip_Rate_Limit_Returns_429()
    {
        using HttpClient client = fx.Factory.CreateClient();
        // PermitLimit = 10 per minute, so the first 10 pass and the 11th is throttled
        for (int i = 0; i < 10; i++)
        {
            HttpResponseMessage ok = await client.SendAsync(Beacon("10.0.0.3", ValidSample));
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }
        HttpResponseMessage limited = await client.SendAsync(Beacon("10.0.0.3", ValidSample));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
