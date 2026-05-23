using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace HurrahTv.Client.Services;

// wraps wwwroot/js/share.js — the IJSObjectReference is loaded once per session
// and disposed when DI tears down the scope. Pages just call ShareOrCopyAsync.
public sealed class ShareService(IJSRuntime js, IConfiguration config) : IAsyncDisposable
{
    // shared links must always point at production, even when running from localhost or
    // staging.hurrah.tv — per issue #67's acceptance criteria. Callers pass relative paths
    // ("/details/tv/123") and the service prepends the canonical origin.
    private const string PublicBaseUrl = "https://hurrah.tv";

    // CI stamps a short SHA into appsettings.json as BuildVersion; JsModulePath.For
    // appends it as ?v={sha} so cached share.js stays in sync with the C# ShareResult
    // contract after a deploy. In dev BuildVersion is absent and the import goes
    // through without a cache-bust (no stale cache to worry about).
    private readonly string _modulePath = JsModulePath.For("./js/share.js", config);

    // serializes the dynamic-import step so two concurrent ShareOrCopyAsync callers don't
    // both fire `import()` and race on _module assignment — the loser would leak its
    // IJSObjectReference and the winner would re-do work the other already did
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IJSObjectReference? _module;
    private bool _disposed;

    public async Task<ShareOutcome> ShareOrCopyAsync(string title, string text, string relativePath)
    {
        if (_disposed) return ShareOutcome.Error;

        IJSObjectReference? module = await GetOrLoadModuleAsync();
        if (module is null) return ShareOutcome.Error;

        // always prepend the canonical production origin — no http(s) bypass. The service
        // guarantees by construction that shared links point at hurrah.tv even from
        // localhost/staging callers.
        string url = PublicBaseUrl + (relativePath.StartsWith('/') ? relativePath : "/" + relativePath);
        try
        {
            ShareResult result = await module.InvokeAsync<ShareResult>("shareOrCopy", new { title, text, url });
            return result.Outcome switch
            {
                "shared" => ShareOutcome.Shared,
                "copied" => ShareOutcome.Copied,
                "cancelled" => ShareOutcome.Cancelled,
                "unsupported" => ShareOutcome.Unsupported,
                _ => ShareOutcome.Error
            };
        }
        catch (TaskCanceledException) { return ShareOutcome.Cancelled; }
        catch (JSDisconnectedException)
        {
            // the JS runtime tore the module reference out from under us (page navigated
            // away, circuit dropped, dev hot-reload swapped the WASM bundle). Drop the
            // cached reference so the next call re-imports against the fresh runtime.
            await TryReleaseModuleAsync();
            return ShareOutcome.Error;
        }
        catch { return ShareOutcome.Error; }
    }

    private async Task<IJSObjectReference?> GetOrLoadModuleAsync()
    {
        // fast path — already loaded, no lock needed
        if (_module is not null) return _module;

        await _initLock.WaitAsync();
        try
        {
            if (_disposed) return null;
            if (_module is not null) return _module;
            try
            {
                _module = await js.InvokeAsync<IJSObjectReference>("import", _modulePath);
                return _module;
            }
            catch
            {
                // import failed (network blip, bad path, JS parse error) — leave _module
                // null so a subsequent call retries cleanly
                return null;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task TryReleaseModuleAsync()
    {
        // a JSDisconnectedException during/after DisposeAsync can re-enter this method
        // from ShareOrCopyAsync's catch block; if DisposeAsync has already torn down the
        // semaphore, WaitAsync will throw ObjectDisposedException — treat as "nothing to
        // release, already disposed" and bail.
        try { await _initLock.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (_module is not null)
            {
                try { await _module.DisposeAsync(); } catch { }
                _module = null;
            }
        }
        finally
        {
            try { _initLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await TryReleaseModuleAsync();
        _initLock.Dispose();
    }

    // System.Text.Json's Web defaults handle camelCase today, but pinning the name
    // with the attribute removes the coupling — one [JsonSerializerOptions] change
    // elsewhere can't quietly break every share by falling through to ShareOutcome.Error
    private sealed record ShareResult([property: JsonPropertyName("outcome")] string Outcome);
}

public enum ShareOutcome { Shared, Copied, Cancelled, Unsupported, Error }
