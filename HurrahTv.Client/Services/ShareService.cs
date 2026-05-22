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

    // CI stamps a short SHA into appsettings.json as BuildVersion; appending it as
    // ?v={sha} to the dynamic import path keeps cached share.js in sync with the
    // C# ShareResult contract after a deploy. In dev BuildVersion is absent and the
    // import goes through without a cache-bust (no stale cache to worry about).
    private readonly string _modulePath = BuildModulePath(config);

    private static string BuildModulePath(IConfiguration config)
    {
        string? version = config["BuildVersion"];
        return string.IsNullOrEmpty(version) ? "./js/share.js" : $"./js/share.js?v={version}";
    }

    private IJSObjectReference? _module;

    public async Task<ShareOutcome> ShareOrCopyAsync(string title, string text, string relativePath)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", _modulePath);
            string url = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : PublicBaseUrl + (relativePath.StartsWith('/') ? relativePath : "/" + relativePath);
            ShareResult result = await _module.InvokeAsync<ShareResult>("shareOrCopy", new { title, text, url });
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
        catch { return ShareOutcome.Error; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch { }
            _module = null;
        }
    }

    // System.Text.Json's Web defaults handle camelCase today, but pinning the name
    // with the attribute removes the coupling — one [JsonSerializerOptions] change
    // elsewhere can't quietly break every share by falling through to ShareOutcome.Error
    private sealed record ShareResult([property: JsonPropertyName("outcome")] string Outcome);
}

public enum ShareOutcome { Shared, Copied, Cancelled, Unsupported, Error }
