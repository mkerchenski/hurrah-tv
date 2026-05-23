namespace HurrahTv.Shared.Filters;

using HurrahTv.Shared.Models;

public static class TrailerFilters
{
    public const int MaxTrailers = 3;
    public const string YouTubeSite = "YouTube";
    public const string TrailerType = "Trailer";

    // pick top trailers: YouTube only, Type=Trailer, official first, then newest.
    // we drop teasers/clips/featurettes so the section is "the official trailer" and
    // not a grab-bag — single-rule predicate keeps the surface coherent.
    public static List<TrailerDto> PickTop(IEnumerable<TrailerDto> videos) =>
        [.. videos
            .Where(v => v.Site == YouTubeSite && v.Type == TrailerType && !string.IsNullOrEmpty(v.Key))
            .OrderByDescending(v => v.Official)
            .ThenByDescending(v => v.PublishedAt ?? DateTime.MinValue)
            .Take(MaxTrailers)];
}
