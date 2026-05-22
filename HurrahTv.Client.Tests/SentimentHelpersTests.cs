using System.Reflection;
using HurrahTv.Client.Helpers;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Tests;

public class SentimentHelpersTests
{
    // helper signature is `Icon(int sentiment)` not `int?` — callers gate on
    // `Sentiment.HasValue` before invoking, so this covers invalid/unexpected
    // values that hit the switch's default arm.
    [Fact]
    public void Icon_DefaultCase_ReturnsEmpty() => Assert.Equal("", SentimentHelpers.Icon(0));

    [Theory]
    [InlineData(SentimentLevel.Down, "icon-[heroicons--hand-thumb-down-20-solid]")]
    [InlineData(SentimentLevel.Up, "icon-[heroicons--hand-thumb-up-20-solid]")]
    [InlineData(SentimentLevel.Favorite, "icon-[heroicons--heart-20-solid]")]
    public void Icon_ReturnsExpectedIcon_ForEverySentimentLevel(int sentiment, string expected)
        => Assert.Equal(expected, SentimentHelpers.Icon(sentiment));

    [Fact]
    public void Color_DefaultCase_ReturnsEmpty() => Assert.Equal("", SentimentHelpers.Color(0));

    [Theory]
    [InlineData(SentimentLevel.Down, "text-red-400")]
    [InlineData(SentimentLevel.Up, "text-green-400")]
    [InlineData(SentimentLevel.Favorite, "text-pink-400")]
    public void Color_ReturnsExpectedColor_ForEverySentimentLevel(int sentiment, string expected)
        => Assert.Equal(expected, SentimentHelpers.Color(sentiment));

    // since SentimentLevel is `static class const int` rather than an enum,
    // Enum.GetValues can't enumerate it — reflection over the public static
    // const fields fills the same role as DisplayOrder_CoversEveryEnumValue
    // does for QueueStatus. adding `SentimentLevel.Loathe = 4` without
    // updating the helpers fails this test instead of silently shipping a
    // missing icon/color.
    [Fact]
    public void Helpers_CoverEverySentimentLevelConstant()
    {
        int[] levels = [.. typeof(SentimentLevel)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .Select(f => (int)f.GetRawConstantValue()!)];

        Assert.NotEmpty(levels);
        foreach (int level in levels)
        {
            Assert.NotEqual("", SentimentHelpers.Icon(level));
            Assert.NotEqual("", SentimentHelpers.Color(level));
        }
    }

    // pins the long-form labels surfaced in the Queue page filter chips and the
    // sentiment dialog header. "Watched" (not "Finished") and "Not For Me"
    // (not "NotForMe") are deliberate UI translations of the enum names.
    [Theory]
    [InlineData(QueueStatus.WantToWatch, "Want to Watch")]
    [InlineData(QueueStatus.Watching, "Watching")]
    [InlineData(QueueStatus.Finished, "Watched")]
    [InlineData(QueueStatus.NotForMe, "Not For Me")]
    public void StatusLabel_ReturnsExpectedLabel_ForEveryStatus(QueueStatus status, string expected)
        => Assert.Equal(expected, SentimentHelpers.StatusLabel(status));

    // StatusLabel defaults to "" — same broken-UI failure mode as
    // BadgeHelpers.StatusShortLabel. force a label decision on new enum values.
    [Fact]
    public void StatusLabel_HasNonEmptyMapping_ForEveryQueueStatus()
    {
        foreach (QueueStatus status in Enum.GetValues<QueueStatus>())
        {
            Assert.NotEqual("", SentimentHelpers.StatusLabel(status));
        }
    }
}
