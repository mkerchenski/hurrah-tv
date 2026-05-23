namespace HurrahTv.Client.Services;

// shared transient-notice ("toast") plumbing. Replaces the duplicated _shareNotice +
// _shareToastCts + ShowShareToastAsync pattern that used to live on Details.razor and
// Settings.razor. Pages call ShowAsync(message); the single <ToastHost /> mounted in
// MainLayout subscribes to OnChanged and renders the toast.
public sealed class ToastService : IDisposable
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(2500);

    public string? Message { get; private set; }
    public event Action? OnChanged;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    public async Task ShowAsync(string message, TimeSpan? duration = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;

        // cancel any in-flight clear-timer so the new toast wins
        CancellationTokenSource? previous = _cts;
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        previous?.Dispose();

        CancellationTokenSource cts = new();
        _cts = cts;
        Message = message;
        OnChanged?.Invoke();

        try { await Task.Delay(duration ?? DefaultDuration, cts.Token); }
        catch (TaskCanceledException) { return; }

        // only clear if we're still the latest toast — a newer ShowAsync may have
        // replaced us during the delay
        if (cts == _cts && !_disposed)
        {
            Message = null;
            OnChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;
    }
}
