using System.Diagnostics;

namespace HurrahTv.Api.Middleware;

// emits a Server-Timing response header carrying the server's pre-flush processing time, so the
// client RUM beacon (#201) can split server cost from bundle download / WASM boot when diagnosing
// slow loads (#200). OnStarting fires just before the headers flush — it captures time up to the
// first byte, which is the slice the beacon subtracts from the browser's TTFB. Registered as the
// outermost middleware so the timer brackets the whole request.
public sealed class ResponseTimingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        long start = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing", $"app;dur={elapsedMs:F1}");
            return Task.CompletedTask;
        });
        return next(context);
    }
}
