namespace HurrahTv.Client.Services;

// shared transient-notice ("toast") plumbing. Replaces the duplicated _shareNotice +
// _shareToastCts + ShowShareToastAsync pattern that used to live on Details.razor and
// Settings.razor. Pages call ShowAsync(message); the single <ToastHost /> mounted in
// MainLayout subscribes to OnChanged and renders the toast.
//
// Registered as Singleton — matches the QuickActionService event-bus pattern and is the
// lifetime explicitly called out in issue #97. In WASM standalone this is functionally
// the same as Scoped (one app-wide scope), but the explicit Singleton avoids any future
// scope-validator surprise and keeps the cross-component message semantically global.
public sealed class ToastService : IDisposable
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(2500);

    public string? Message { get; private set; }
    public event Action? OnChanged;

    // serializes the cancel-prev / publish-new step so two overlapping ShowAsync calls
    // don't both read the same _cts, both dispose it, and orphan one CancellationTokenSource.
    // The body of the await Task.Delay runs outside the lock — only the swap is critical.
    private readonly System.Threading.Lock _swapLock = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public async Task ShowAsync(string message, TimeSpan? duration = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;

        CancellationTokenSource cts = new();
        CancellationTokenSource? previous;
        lock (_swapLock)
        {
            if (_disposed) { cts.Dispose(); return; }
            previous = _cts;
            _cts = cts;
            Message = message;
        }

        // cancel + dispose the previous timer outside the lock so a slow cancellation
        // can't stall a concurrent ShowAsync waiting on _swapLock
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        previous?.Dispose();

        OnChanged?.Invoke();

        try { await Task.Delay(duration ?? DefaultDuration, cts.Token); }
        catch (TaskCanceledException) { return; }

        // only clear if we're still the latest toast — a newer ShowAsync may have
        // replaced us during the delay
        lock (_swapLock)
        {
            if (cts != _cts || _disposed) return;
            Message = null;
        }
        OnChanged?.Invoke();
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_swapLock)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts;
            _cts = null;
            // null Message on dispose so a re-bound ToastHost on a future navigation can't
            // briefly render a stale toast from the previous page's not-yet-cleared timer
            Message = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
    }
}
