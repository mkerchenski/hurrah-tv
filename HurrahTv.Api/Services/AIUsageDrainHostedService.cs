using System.Collections.Concurrent;

namespace HurrahTv.Api.Services;

// tracks in-flight detached AIUsage writes (CurationService.TrackUsageDetachedAsync)
// and drains them on host shutdown with a bounded timeout. Anthropic was already paid
// for the inference, so the cost row landing is non-negotiable — without this the
// Task.Run continuation either runs against a partially-disposed root provider (logged
// inner-catch) or never runs (process exits before the thread-pool picks it up).
// SIGTERM / app-pool recycle / Azure deploy slot swap are exactly when in-flight
// writes are most common — every running request flushes through the same window.
// pins #123.
//
// shape: singleton-scoped, exposes Run(Func<CT, Task>) so registration is ATOMIC with
// task creation — the in-flight TaskCompletionSource is added to the dict BEFORE the
// thread-pool dispatch happens, so a concurrent StopAsync snapshot can't miss it.
// ConcurrentDictionary entries are removed on completion via ContinueWith. StopAsync
// snapshots and uses Task.WaitAsync(linked CT) so the WhenAll fault propagates and
// the timeout timer is disposed when drain wins (no orphan Task.Delay leak).
public sealed partial class AIUsageDrainHostedService(ILogger<AIUsageDrainHostedService> logger) : IHostedService
{
    // host drain budget. Azure's deploy graceful-stop budget is ~10s before SIGKILL.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    // each inner write gets a slightly tighter timeout so it terminates before the
    // outer drain budget, leaving room for the drain to log completion rather than
    // racing the host's hard kill.
    private static readonly TimeSpan InnerWriteTimeout = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly ILogger<AIUsageDrainHostedService> _logger = logger;

    // Run owns the dispatch so the in-flight task is registered BEFORE thread-pool
    // scheduling — closes the Register-after-Task.Run race window where a concurrent
    // StopAsync could snapshot empty between Task.Run returning and the caller's
    // separate Register call.
    public Task Run(Func<CancellationToken, Task> work)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // add BEFORE Task.Run schedules — once Run returns the tcs.Task is observable
        // to any concurrent StopAsync. Remove via ContinueWith so the dict doesn't
        // accumulate completed tasks across the lifetime of the process.
        _inFlight.TryAdd(tcs.Task, 0);
        _ = tcs.Task.ContinueWith(static (t, state) =>
            ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _inFlight, TaskScheduler.Default);

        _ = Task.Run(async () =>
        {
            using CancellationTokenSource cts = new(InnerWriteTimeout);
            try
            {
                await work(cts.Token);
            }
            catch
            {
                // work() is expected to handle/log its own exceptions; this catch is
                // defense-in-depth so a logger throw during host shutdown doesn't escape
                // as an unobserved task fault (which the drain's Task.WhenAll would then
                // surface as an AggregateException at the wrong layer).
            }
            finally
            {
                tcs.TrySetResult();
            }
        });

        return tcs.Task;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] snapshot = [.. _inFlight.Keys];
        if (snapshot.Length == 0) return;

        // host-driven shutdown CT is already signalled — drain budget is zero. Log
        // honestly rather than misreporting as a 10s timeout (which is what would
        // happen if we entered the Task.Delay race with a pre-cancelled CT).
        if (cancellationToken.IsCancellationRequested)
        {
            LogDrainAborted(snapshot.Length);
            return;
        }

        LogDraining(snapshot.Length);

        using CancellationTokenSource timeoutCts = new(DrainTimeout);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            // WaitAsync(linkedCts.Token) awaits the WhenAll task with cancellation —
            // disposing the linked CTS cancels the timeout timer when drain wins, so
            // no orphan Task.Delay is left running. Faults in the inner snapshot tasks
            // surface as exceptions here (rather than being silently held in the drain
            // Task and observed only by reference comparison).
            await Task.WhenAll(snapshot).WaitAsync(linkedCts.Token);
            LogDrainComplete();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // count snapshot tasks that haven't completed yet — concurrent Register
            // additions are excluded since they aren't in the snapshot.
            int remaining = snapshot.Count(t => !t.IsCompleted);
            LogDrainTimeout(DrainTimeout.TotalSeconds, remaining);
        }
        catch (OperationCanceledException)
        {
            int remaining = snapshot.Count(t => !t.IsCompleted);
            LogDrainAborted(remaining);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Draining {Count} in-flight AIUsage writes on shutdown")]
    private partial void LogDraining(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "AIUsage drain complete")]
    private partial void LogDrainComplete();

    [LoggerMessage(Level = LogLevel.Warning, Message = "AIUsage drain timed out after {Timeout}s — {Remaining} writes may be lost")]
    private partial void LogDrainTimeout(double timeout, int remaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AIUsage drain aborted by host shutdown — {Remaining} writes may be lost")]
    private partial void LogDrainAborted(int remaining);
}
