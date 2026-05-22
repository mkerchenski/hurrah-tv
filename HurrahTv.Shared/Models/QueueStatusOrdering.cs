namespace HurrahTv.Shared.Models;

// canonical queue-status ordering. Both the Client UI (Queue.razor tabs, QuickActions,
// Home watchlist sort) and the Api SQL (DbService.GetQueueAsync ORDER BY CASE) sort by
// this rule — promoted here so neither side can drift. SQL keeps a literal CASE for
// readability; its comment references this type so a grep on either name finds both.
public static class QueueStatusOrdering
{
    // Array.AsReadOnly returns a ReadOnlyCollection<T> whose runtime type is NOT the
    // underlying array — a down-cast to QueueStatus[] throws, so callers can't mutate
    // even via reflection-free casts. Required in long-lived Blazor WASM processes
    // where a corrupted static persists for the entire browser session.
    // See: Learnings/blazor-wasm-static-mutable-arrays.md
    public static readonly IReadOnlyList<QueueStatus> DisplayOrder = Array.AsReadOnly<QueueStatus>(
    [
        QueueStatus.Watching,
        QueueStatus.WantToWatch,
        QueueStatus.Finished,
        QueueStatus.NotForMe
    ]);

    // SortPriority is derived from DisplayOrder so the list is the only place the rule
    // is written down — reordering DisplayOrder automatically reorders the sort keys.
    private static readonly Dictionary<QueueStatus, int> _priority =
        DisplayOrder.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => t.i);

    // 0 = first in display order; unknown statuses sort after every known value.
    public static int SortPriority(QueueStatus status) =>
        _priority.TryGetValue(status, out int p) ? p : DisplayOrder.Count;
}
