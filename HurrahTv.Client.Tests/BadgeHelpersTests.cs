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

    [Theory]
    [InlineData(QueueStatus.WantToWatch, "bg-gray-700/80")]
    [InlineData(QueueStatus.Watching, "bg-green-600/80")]
    [InlineData(QueueStatus.Finished, "bg-blue-600/80")]
    [InlineData(QueueStatus.NotForMe, "bg-gray-600/80")]
    public void StatusBg_ReturnsExpectedClass_ForEveryStatus(QueueStatus status, string expected)
        => Assert.Equal(expected, BadgeHelpers.StatusBg(status));

    [Theory]
    [InlineData(QueueStatus.WantToWatch, "text-accent")]
    [InlineData(QueueStatus.Watching, "text-green-400")]
    [InlineData(QueueStatus.Finished, "text-blue-400")]
    [InlineData(QueueStatus.NotForMe, "text-gray-400")]
    public void StatusColor_ReturnsExpectedClass_ForEveryStatus(QueueStatus status, string expected)
        => Assert.Equal(expected, BadgeHelpers.StatusColor(status));

    [Theory]
    [InlineData(QueueStatus.WantToWatch, "ring-accent")]
    [InlineData(QueueStatus.Watching, "ring-green-500")]
    [InlineData(QueueStatus.Finished, "ring-blue-500")]
    [InlineData(QueueStatus.NotForMe, "ring-gray-500")]
    public void StatusRing_ReturnsExpectedClass_ForEveryStatus(QueueStatus status, string expected)
        => Assert.Equal(expected, BadgeHelpers.StatusRing(status));

    // pins the deliberate "Finished" enum → "Watched" UI label translation
    // (see comment on QueueStatus.Finished in QueueItem.cs). a refactor that
    // "fixes" the inconsistency by renaming to "Finished" should fail loudly.
    [Theory]
    [InlineData(QueueStatus.WantToWatch, "Want")]
    [InlineData(QueueStatus.Watching, "Watching")]
    [InlineData(QueueStatus.Finished, "Watched")]
    [InlineData(QueueStatus.NotForMe, "Nope")]
    public void StatusShortLabel_ReturnsExpectedLabel_ForEveryStatus(QueueStatus status, string expected)
        => Assert.Equal(expected, BadgeHelpers.StatusShortLabel(status));

    // StatusShortLabel defaults to "" — unlike the visual-class helpers which fall back
    // to a gray neutral, a missing label would ship as blank text in the UI. this guard
    // forces a label decision when a new QueueStatus is added.
    [Fact]
    public void StatusShortLabel_HasNonEmptyMapping_ForEveryQueueStatus()
    {
        foreach (QueueStatus status in Enum.GetValues<QueueStatus>())
        {
            Assert.NotEqual("", BadgeHelpers.StatusShortLabel(status));
        }
    }
}
