namespace HurrahTv.Client.Services;

// per-control debounced auto-save for Settings. Each control owns one FieldSaver: a
// change schedules a save after a debounce window (coalescing rapid toggles into one
// PUT), and a version counter ensures a stale in-flight save can't stamp its result
// over a newer change — the off→on→off race ends at off. Replaces the page-wide Save
// button. See #102.
public sealed class FieldSaver(Func<Task> stateChanged, int debounceMs = 250) : IDisposable
{
    public enum Status { Idle, Saving, Saved, Failed }

    private static readonly TimeSpan SavedDisplay = TimeSpan.FromMilliseconds(1500);

    private CancellationTokenSource? _debounceCts;
    private Func<CancellationToken, Task>? _lastSave;
    private int _version;
    private bool _disposed;

    public Status State { get; private set; }

    // schedule (or reschedule) a save. Cancels any pending debounce so only the latest
    // value within the window fires; a save already past the debounce is cancelled too,
    // abandoning its now-stale request.
    public void Schedule(Func<CancellationToken, Task> save) => _ = RunAsync(save, immediate: false);

    // retry re-runs the last save immediately, skipping the debounce.
    public void Retry()
    {
        if (_lastSave is { } save) _ = RunAsync(save, immediate: true);
    }

    private async Task RunAsync(Func<CancellationToken, Task> save, bool immediate)
    {
        _lastSave = save;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        CancellationTokenSource cts = new();
        _debounceCts = cts;
        CancellationToken token = cts.Token; // capture before any later Dispose invalidates cts.Token

        if (!immediate)
        {
            try { await Task.Delay(debounceMs, token); }
            catch (OperationCanceledException) { return; }
        }

        int version = ++_version;
        State = Status.Saving;
        await Notify();

        try
        {
            await save(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return; // our own cancellation — a newer save superseded this one and owns the state
        }
        catch
        {
            if (version == _version)
            {
                State = Status.Failed;
                await Notify();
            }
            return;
        }

        if (version != _version) return;
        State = Status.Saved;
        await Notify();

        try { await Task.Delay(SavedDisplay, token); }
        catch (OperationCanceledException) { return; } // disposed or superseded during the Saved display

        if (version == _version && State == Status.Saved)
        {
            State = Status.Idle;
            await Notify();
        }
    }

    private async Task Notify()
    {
        if (_disposed) return;
        await stateChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        try { _debounceCts?.Cancel(); } catch (ObjectDisposedException) { }
        _debounceCts?.Dispose();
    }
}
