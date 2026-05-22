using HurrahTv.Client.Helpers;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Tests;

public class BadgeHelpersTests
{
    [Theory]
    [InlineData(QueueStatus.WantToWatch, "icon-[heroicons--bookmark-20-solid]")]
    [InlineData(QueueStatus.Watching, "icon-[heroicons--play-20-solid]")]
    [InlineData(QueueStatus.Finished, "icon-[heroicons--check-20-solid]")]
    [InlineData(QueueStatus.NotForMe, "icon-[heroicons--no-symbol-20-solid]")]
    public void StatusIcon_ReturnsExpectedIcon_ForEveryStatus(QueueStatus status, string expected)
        => Assert.Equal(expected, BadgeHelpers.StatusIcon(status));

    // source of truth for status ordering across Queue.razor and QuickActions.
    // a silent reorder would ship a broken UI — pin the exact display order
    // (which is intentionally not the enum's numeric order).
    [Fact]
    public void AllStatuses_PinsDisplayOrder()
    {
        QueueStatus[] expected =
        [
            QueueStatus.Watching,
            QueueStatus.WantToWatch,
            QueueStatus.Finished,
            QueueStatus.NotForMe
        ];

        Assert.Equal(expected, BadgeHelpers.AllStatuses);
    }

    // when a new QueueStatus is added to the enum, this test forces a decision
    // about where it lives in the UI ordering — failing here is a feature.
    [Fact]
    public void AllStatuses_CoversEveryEnumValue()
    {
        QueueStatus[] enumValues = Enum.GetValues<QueueStatus>();
        Assert.Equal(enumValues.Length, BadgeHelpers.AllStatuses.Count);
        Assert.Equal(
            [.. enumValues.OrderBy(s => s)],
            [.. BadgeHelpers.AllStatuses.OrderBy(s => s)]);
    }

    // the [Theory] above pins exact icons for the four currently-defined statuses.
    // this guard forces a decision when a new QueueStatus is added — without it,
    // a 5th enum value would silently get StatusIcon's default "" arm and ship.
    // closes the issue-#83 acceptance criterion "cover StatusIcon for every
    // QueueStatus value" in a way that survives future enum growth.
    [Fact]
    public void StatusIcon_HasNonEmptyMapping_ForEveryQueueStatus()
    {
        foreach (QueueStatus status in Enum.GetValues<QueueStatus>())
        {
            Assert.NotEqual("", BadgeHelpers.StatusIcon(status));
        }
    }
}
