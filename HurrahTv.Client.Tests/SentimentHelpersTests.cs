using HurrahTv.Client.Helpers;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Tests;

public class SentimentHelpersTests
{
    // sentiment 0 is the unset case — no badge should render.
    [Fact]
    public void Icon_Unset_ReturnsEmpty()
    {
        Assert.Equal("", SentimentHelpers.Icon(0));
    }

    [Theory]
    [InlineData(SentimentLevel.Down, "icon-[heroicons--hand-thumb-down-20-solid]")]
    [InlineData(SentimentLevel.Up, "icon-[heroicons--hand-thumb-up-20-solid]")]
    [InlineData(SentimentLevel.Favorite, "icon-[heroicons--heart-20-solid]")]
    public void Icon_ReturnsExpectedIcon_ForEverySentimentLevel(int sentiment, string expected)
    {
        Assert.Equal(expected, SentimentHelpers.Icon(sentiment));
    }

    [Fact]
    public void Color_Unset_ReturnsEmpty()
    {
        Assert.Equal("", SentimentHelpers.Color(0));
    }

    [Theory]
    [InlineData(SentimentLevel.Down, "text-red-400")]
    [InlineData(SentimentLevel.Up, "text-green-400")]
    [InlineData(SentimentLevel.Favorite, "text-pink-400")]
    public void Color_ReturnsExpectedColor_ForEverySentimentLevel(int sentiment, string expected)
    {
        Assert.Equal(expected, SentimentHelpers.Color(sentiment));
    }
}
