using HurrahTv.Shared.Filters;
using HurrahTv.Shared.Models;

namespace HurrahTv.Shared.Tests;

// pins the Details-page trailer rule: YouTube + Type=Trailer only, official before
// unofficial, newest first, capped at 3. Catches the silent-drop bugs (wrong-site
// videos sneaking through, sort key inverting on null PublishedAt).
public class TrailerFiltersTests
{
    private static TrailerDto Video(
        string key = "abc",
        string name = "Official Trailer",
        string site = "YouTube",
        string type = "Trailer",
        bool official = true,
        DateTime? publishedAt = null) =>
        new() { Key = key, Name = name, Site = site, Type = type, Official = official, PublishedAt = publishedAt };

    [Fact]
    public void Drops_Non_YouTube_Sites()
    {
        List<TrailerDto> input = [Video(key: "yt"), Video(key: "vimeo", site: "Vimeo")];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Single(result);
        Assert.Equal("yt", result[0].Key);
    }

    [Fact]
    public void Drops_Non_Trailer_Types()
    {
        List<TrailerDto> input =
        [
            Video(key: "trailer"),
            Video(key: "teaser", type: "Teaser"),
            Video(key: "clip", type: "Clip"),
            Video(key: "feat", type: "Featurette")
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Single(result);
        Assert.Equal("trailer", result[0].Key);
    }

    [Fact]
    public void Drops_Entries_With_Empty_Key()
    {
        // protects the iframe URL — an empty key would 404 the YouTube embed
        List<TrailerDto> input = [Video(key: ""), Video(key: "good")];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Single(result);
        Assert.Equal("good", result[0].Key);
    }

    [Fact]
    public void Sorts_Official_Before_Unofficial_Then_Newest_First()
    {
        DateTime older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime newer = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<TrailerDto> input =
        [
            Video(key: "unofficial-newer", official: false, publishedAt: newer),
            Video(key: "official-older", official: true, publishedAt: older),
            Video(key: "official-newer", official: true, publishedAt: newer),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["official-newer", "official-older", "unofficial-newer"], result.Select(t => t.Key));
    }

    [Fact]
    public void Null_PublishedAt_Sorts_Last_Within_Same_Official_Bucket()
    {
        // null dates would float to the top if we naively descending-sorted on a nullable —
        // explicit DateTime.MinValue fallback pins them last
        DateTime published = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<TrailerDto> input =
        [
            Video(key: "null-date", publishedAt: null),
            Video(key: "dated", publishedAt: published),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["dated", "null-date"], result.Select(t => t.Key));
    }

    [Fact]
    public void Caps_At_MaxTrailers()
    {
        List<TrailerDto> input = [.. Enumerable.Range(0, 10).Select(i => Video(key: $"t{i}"))];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(TrailerFilters.MaxTrailers, result.Count);
    }

    [Fact]
    public void Empty_Input_Returns_Empty() => Assert.Empty(TrailerFilters.PickTop([]));
}
