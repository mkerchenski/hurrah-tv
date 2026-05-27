namespace HurrahTv.Client.Services;

// per-control debounced auto-save for Settings. A change debounces (coalescing rapid toggles
// into one request), then enters a single-slot queue drained by one worker: at most one save is
// in flight at a time, and a newer change runs only after the current one completes. Because
// requests never overlap, they reach the server in order — so the off->on->off race ends at off
// on the server, not just in the UI. This matters because the settings PUTs don't observe request
// aborts, so cancelling an in-flight request wouldn't stop a slower earlier write from committing
// after a newer one. Saves therefore run to completion (no per-save cancellation) and the latest
// queued change always wins. Replaces the page-wide Save button. See #102.
public sealed class FieldSaver(Func<Task> stateChanged, int debounceMs = 250) : IDisposable
{
    public enum Status { Idle, Saving, Saved, Failed }

    private static readonly TimeSpan SavedDisplay = TimeSpan.FromMilliseconds(1500);

    private CancellationTokenSource? _debounceCts;
    private Func<Task>? _pending; // newest debounced save waiting to run; a not-yet-started one is replaced
    private Func<Task>? _lastSave; // for Retry
    private bool _draining;
    private bool _disposed;

    public Status State { get; private set; }

    // schedule a save after the debounce window. rapid calls within the window coalesce — only the
    // last one runs.
    public void Schedule(Func<Task> save)
    {
        _lastSave = save;
        _ = DebounceThenQueue(save);
    }

    // retry re-runs the last save immediately, skipping the debounce.
    public void Retry()
    {
        if (_lastSave is { } save) QueueAndDrain(save);
    }

    private async Task DebounceThenQueue(Func<Task> save)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        CancellationTokenSource cts = new();
        _debounceCts = cts;
        try { await Task.Delay(debounceMs, cts.Token); }
        catch (OperationCanceledException) { return; } // a newer change superseded this one within the window

        QueueAndDrain(save);
    }

    private void QueueAndDrain(Func<Task> save)
    {
        _pending = save; // newest wins; a not-yet-started pending save is dropped
        _ = Drain();
    }

    private async Task Drain()
    {
        if (_draining) return; // a worker is already running; it will pick up _pending
        _draining = true;
        try
        {
            while (_pending is { } save)
            {
                _pending = null;
                State = Status.Saving;
                await Notify();

                bool ok;
                try { await save(); ok = true; }
                catch { ok = false; }

                if (_pending is not null) continue; // a newer change arrived during the save — run it next

                if (!ok)
                {
                    State = Status.Failed;
                    await Notify();
                    if (_pending is not null) continue; // newer change arrived during the failed-state notify
                    return; // surfaced Failed; Retry (or a later change) re-enters the drain
                }

                State = Status.Saved;
                await Notify();
                await Task.Delay(SavedDisplay);
                if (_pending is null && State == Status.Saved)
                {
                    State = Status.Idle;
                    await Notify();
                }
            }
        }
        finally { _draining = false; }
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
