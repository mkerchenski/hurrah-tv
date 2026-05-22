using Microsoft.JSInterop;

namespace HurrahTv.Client.Services;

// wraps wwwroot/js/share.js — the IJSObjectReference is loaded once per session
// and disposed when DI tears down the scope. Pages just call ShareOrCopyAsync.
public sealed class ShareService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<ShareOutcome> ShareOrCopyAsync(string title, string text, string url)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/share.js");
            ShareResult result = await _module.InvokeAsync<ShareResult>("shareOrCopy", new { title, text, url });
            return result.Outcome switch
            {
                "shared"      => ShareOutcome.Shared,
                "copied"      => ShareOutcome.Copied,
                "cancelled"   => ShareOutcome.Cancelled,
                "unsupported" => ShareOutcome.Unsupported,
                _             => ShareOutcome.Error
            };
        }
        catch
        {
            return ShareOutcome.Error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch { }
            _module = null;
        }
    }

    private sealed record ShareResult(string Outcome);
}

public enum ShareOutcome { Shared, Copied, Cancelled, Unsupported, Error }
