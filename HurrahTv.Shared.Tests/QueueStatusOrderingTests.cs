using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// pins #91 — the queue-status ordering rule moved from Client/Helpers/BadgeHelpers to
// HurrahTv.Shared so both the Client UI (Queue.razor tabs, QuickActions, Home sort)
// and the Api SQL (DbService.GetQueueAsync ORDER BY CASE) sort by the same list.
// Tests live in Shared.Tests because the invariant is no longer Client-only.
public class QueueStatusOrderingTests
{
    // source of truth for status ordering across all UI surfaces and the SQL CASE.
    // a silent reorder would ship a broken UI — pin the exact display order
    // (which is intentionally not the enum's numeric order).
    [Fact]
    public void DisplayOrder_PinsExactOrdering()
    {
        QueueStatus[] expected =
        [
            QueueStatus.Watching,
            QueueStatus.WantToWatch,
            QueueStatus.Finished,
            QueueStatus.NotForMe
        ];

        Assert.Equal(expected, QueueStatusOrdering.DisplayOrder);
    }

    // when a new QueueStatus is added to the enum, this test forces a decision
    // about where it lives in the UI ordering — failing here is a feature.
    [Fact]
    public void DisplayOrder_CoversEveryEnumValue()
    {
        QueueStatus[] enumValues = Enum.GetValues<QueueStatus>();
        Assert.Equal(enumValues.Length, QueueStatusOrdering.DisplayOrder.Count);
        Assert.Equal(
            [.. enumValues.OrderBy(s => s)],
            [.. QueueStatusOrdering.DisplayOrder.OrderBy(s => s)]);
    }

    // SortPriority is derived from DisplayOrder via a dictionary — this test verifies
    // the derivation invariant so a refactor that uncouples them (e.g. hardcoding the
    // switch) loudly fails. DisplayOrder.IndexOf is the spec.
    [Fact]
    public void SortPriority_MatchesDisplayOrderIndex_ForEveryStatus()
    {
        for (int i = 0; i < QueueStatusOrdering.DisplayOrder.Count; i++)
        {
            QueueStatus status = QueueStatusOrdering.DisplayOrder[i];
            Assert.Equal(i, QueueStatusOrdering.SortPriority(status));
        }
    }

    // Watching is the highest-priority status — pinning this independently means a
    // reorder of DisplayOrder that accidentally demotes Watching would fail two tests
    // (this one and DisplayOrder_PinsExactOrdering), making the regression unambiguous.
    [Fact]
    public void SortPriority_Watching_IsZero()
        => Assert.Equal(0, QueueStatusOrdering.SortPriority(QueueStatus.Watching));
}
