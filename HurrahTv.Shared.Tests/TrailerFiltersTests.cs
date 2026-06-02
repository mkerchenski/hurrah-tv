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
    public void Named_Official_Beats_Newer_Unofficial_When_Neither_Is_Flagged()
    {
        // pins #111: TMDb often leaves the real studio trailer flag-less. The old sort
        // (flag, then newest) let a newer fan upload win. The name "Official Trailer"
        // must outrank a plain newer "Trailer" even though both have official=false.
        DateTime older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime newer = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<TrailerDto> input =
        [
            Video(key: "fan-newer", name: "Trailer", official: false, publishedAt: newer),
            Video(key: "official-older", name: "Official Trailer", official: false, publishedAt: older),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["official-older", "fan-newer"], result.Select(t => t.Key));
    }

    [Fact]
    public void Flagged_Official_Beats_NameOnly_Official()
    {
        // pins #111: the TMDb flag is a stronger signal than the name alone, so a
        // flagged-but-unnamed official outranks an unflagged "Official ..." by name.
        List<TrailerDto> input =
        [
            Video(key: "name-only", name: "Official Trailer", official: false),
            Video(key: "flagged", name: "Launch Trailer", official: true),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["flagged", "name-only"], result.Select(t => t.Key));
    }

    [Fact]
    public void Flagged_ASL_Trailer_Loses_To_Clean_Official_Trailer()
    {
        // pins #111: TMDb tags ASL (sign-language) cuts official=true. A clean "Official
        // Trailer" must default ahead of an "Official Trailer (ASL)" even when both are
        // flagged official and the ASL one is newer.
        DateTime older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime newer = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<TrailerDto> input =
        [
            Video(key: "asl", name: "Official Trailer (ASL)", official: true, publishedAt: newer),
            Video(key: "clean", name: "Official Trailer", official: true, publishedAt: older),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["clean", "asl"], result.Select(t => t.Key));
    }

    [Fact]
    public void Clean_Official_Beats_Flagged_Marker_Even_When_Clean_Is_Unflagged()
    {
        // pins #111: "official asl < official" — an alt-format cut loses to the primary
        // trailer even if TMDb only flagged the alt cut. tier 3 (named, unmarked) > tier 2
        // (flagged, marked).
        List<TrailerDto> input =
        [
            Video(key: "asl-flagged", name: "Official Trailer (ASL)", official: true),
            Video(key: "clean-unflagged", name: "Official Trailer", official: false),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["clean-unflagged", "asl-flagged"], result.Select(t => t.Key));
    }

    [Fact]
    public void Marker_Matches_Whole_Word_Only_Not_Substrings()
    {
        // \b boundaries: "ASL" must not fire inside an unrelated word. "Aslan" contains the
        // letters a-s-l, so a naive Contains would wrongly demote a real Narnia trailer.
        List<TrailerDto> input =
        [
            Video(key: "aslan", name: "Aslan Returns Official Trailer", official: true),
            Video(key: "asl", name: "Official Trailer (ASL)", official: true),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        // "aslan" stays a primary (tier 4); only the real "(ASL)" cut is demoted (tier 2)
        Assert.Equal(["aslan", "asl"], result.Select(t => t.Key));
    }

    [Fact]
    public void Unofficial_Is_Not_Treated_As_Named_Official()
    {
        // pins #111: "official" is matched as a whole word, so "Unofficial Trailer" must not
        // get the named-official boost — it ranks as a plain unflagged video (tier 0) and
        // loses to a real flagged official.
        List<TrailerDto> input =
        [
            Video(key: "unofficial", name: "Unofficial Trailer", official: false),
            Video(key: "official", name: "Launch Trailer", official: true),
        ];

        List<TrailerDto> result = TrailerFilters.PickTop(input);

        Assert.Equal(["official", "unofficial"], result.Select(t => t.Key));
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
