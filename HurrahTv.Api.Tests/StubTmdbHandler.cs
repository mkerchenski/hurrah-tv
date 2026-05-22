using System.Net;

namespace HurrahTv.Api.Tests;

// no-op handler that intercepts all TmdbService HTTP calls in tests so we don't
// hit api.themoviedb.org. RefreshStaleItemsInBackground has per-item try/catch,
// so a 404 here just gets swallowed and logged at warning — no live network
// dependency in CI, no rate-limit risk against our real TMDb key.
internal sealed class StubTmdbHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        });
}
