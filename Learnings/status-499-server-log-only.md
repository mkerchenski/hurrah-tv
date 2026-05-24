# 499 Client Closed Request Is a Server-Log Signal, Not a Client Contract

> **Area:** API | WASM
> **Date:** 2026-05-24
> **Resolves:** mkerchenski/hurrah-tv#117

## Context
PR #118's `/match` endpoint catches client-disconnect cancellation and returns `Results.StatusCode(StatusCodes.Status499ClientClosedRequest)` instead of the generic `200 OK` with `null` body that other error paths use. The intent: distinguish "the user navigated away" from "the AI said no match" in server logs and metrics.

A reviewer agent noticed the client side can't actually observe the 499. Filed as follow-up #120 to decide between documenting it as server-only vs. translating it on the client.

## Learning
On the wire, `499` is a [nginx-defined status code](https://www.nginx.com/resources/wiki/extending/api/http/) that ASP.NET also exposes via `StatusCodes.Status499ClientClosedRequest`. It exists for one reason: server-side request logs and middleware metrics need a way to bucket "client gave up" separately from real `5xx` errors or successful `null` responses. Treat it as a label on a row in your access log, not a message to the client.

Three reasons the cancelling client never sees it:

1. **The client cancelled.** If our `cancellationToken` triggered the abort, `HttpClient` raised `OperationCanceledException` *before* the response could be read. The 499 was written to a TCP socket that's no longer being read.
2. **Even if the client survives long enough to receive bytes,** `HttpClient.GetFromJsonAsync` calls `EnsureSuccessStatusCode()` internally — `499` is non-2xx, so it throws `HttpRequestException`. From the caller's perspective, that's identical to a connection-reset or DNS-fail.
3. **The existing bare `catch { return null; }` in `ApiClient` curation methods catches that `HttpRequestException` and returns `null`** — indistinguishable from "AI said no match available."

So if you write `499` expecting the client to special-case it, you're wasting bytes. Either:

```csharp
// (a) accept it as server-log-only and document the intent inline
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    // 499 is for server-side request-log bucketing — the cancelling client
    // never reads this response. don't expect any client-side branching on it.
    return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
}

// (b) actually translate it in ApiClient so callers see OCE consistently
//     (more work — needs detecting StatusCode == 499 inside the catch)
catch (HttpRequestException ex) when (ex.StatusCode == (HttpStatusCode)499)
{
    throw new OperationCanceledException(cancellationToken);
}
```

If you only want telemetry separation, (a) is the right answer. If you want unified client-side cancel handling, (b) is required, and at that point you might as well skip 499 and have the server throw OCE → log middleware buckets it.

## Example
On `HurrahTv.Api`, hurrah uses ASP.NET's default request log. A line like:

```
HTTP GET /api/curation/match/tv/1399 responded 499 in 47ms
```

is the *only* place the 499 currently shows up. App Insights / Kestrel access logs / nginx-in-front-of-azure-app-service all bucket on status code. If a future operator wants "how many cancelled AI requests per hour," that query works only because of the 499. The client UI is unaffected either way.
