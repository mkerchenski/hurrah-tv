using HurrahTv.Client.Services;

namespace HurrahTv.Client.Tests;

// FieldSaver runs in single-threaded Blazor WASM. These tests emulate that by driving it inline:
// debounceMs:0 makes the debounce delay complete synchronously, and gating each save on a
// TaskCompletionSource (whose continuations run inline on the thread that completes it) keeps every
// step on the test thread — no real waiting, fully deterministic. Pins the last-write-wins
// serialization that closes the overlapping-PUT race in #102 / PR #143: saves never overlap, so the
// server commits them in order even though it doesn't observe request aborts.
public class FieldSaverTests
{
    private static FieldSaver NewSaver() => new(() => Task.CompletedTask, debounceMs: 0);

    [Fact]
    public void Saves_DoNotOverlap_SecondWaitsForFirstToComplete()
    {
        List<int> started = [];
        TaskCompletionSource gate1 = new();
        TaskCompletionSource gate2 = new();
        FieldSaver saver = NewSaver();

        saver.Schedule(async () => { started.Add(1); await gate1.Task; });
        Assert.Equal(new[] { 1 }, started); // first save starts immediately

        saver.Schedule(async () => { started.Add(2); await gate2.Task; });
        Assert.Equal(new[] { 1 }, started); // second is queued, not started — one request in flight at a time

        gate1.SetResult();                  // first completes -> the drain runs the queued second
        Assert.Equal(new[] { 1, 2 }, started);

        saver.Dispose();                    // second left parked on gate2 — abandoned, no background work
    }

    [Fact]
    public void RapidChanges_WhileSaving_OnlyTheLatestQueuedRuns()
    {
        List<int> started = [];
        TaskCompletionSource gate1 = new();
        TaskCompletionSource gate4 = new();
        FieldSaver saver = NewSaver();

        saver.Schedule(async () => { started.Add(1); await gate1.Task; });
        Assert.Equal(new[] { 1 }, started);

        // three more changes arrive while #1 is in flight; only the last (#4) should run next
        saver.Schedule(() => { started.Add(2); return Task.CompletedTask; });
        saver.Schedule(() => { started.Add(3); return Task.CompletedTask; });
        saver.Schedule(async () => { started.Add(4); await gate4.Task; });
        Assert.Equal(new[] { 1 }, started); // still only #1 has started

        gate1.SetResult();
        Assert.Equal(new[] { 1, 4 }, started); // #2 and #3 were superseded in the queue; last write wins

        saver.Dispose();
    }

    [Fact]
    public void FailedSave_SurfacesFailedState()
    {
        FieldSaver saver = NewSaver();

        // a non-success PUT surfaces as a thrown save (see Settings.razor); FieldSaver must show Failed
        // so the inline "Couldn't save" + Retry affordance appears
        saver.Schedule(() => Task.FromException(new InvalidOperationException("save failed")));
        Assert.Equal(FieldSaver.Status.Failed, saver.State);

        saver.Dispose();
    }
}
