namespace HurrahTv.Shared.Filters;

using System.Text.RegularExpressions;
using HurrahTv.Shared.Models;

public static partial class TrailerFilters
{
    public const int MaxTrailers = 3;
    public const string YouTubeSite = "YouTube";
    public const string TrailerType = "Trailer";

    // accessibility / alternate-format cuts (American Sign Language, audio-described, ASMR
    // re-reads). TMDb contributors frequently still tag these official=true, but they aren't
    // the primary trailer, so OfficialRank demotes them one tier below an unmarked official.
    // matched as whole words (case-insensitive) so "ASL" hits "Official Trailer (ASL)" but
    // never a substring like "phantasm". add new markers here. pins #111.
    [GeneratedRegex(@"\b(ASL|ASMR|sign language|audio[- ]?descri(bed|ption|ptive))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DemotedMarkerPattern();

    // pick top trailers: YouTube only, Type=Trailer, most-official first, then newest.
    // we drop teasers/clips/featurettes so the section is "the official trailer" and
    // not a grab-bag — single-rule predicate keeps the surface coherent.
    public static List<TrailerDto> PickTop(IEnumerable<TrailerDto> videos) =>
        [.. videos
            .Where(v => v.Site == YouTubeSite && v.Type == TrailerType && !string.IsNullOrEmpty(v.Key))
            .OrderByDescending(OfficialRank)
            .ThenByDescending(v => v.PublishedAt ?? DateTime.MinValue)
            .Take(MaxTrailers)];

    // rank a video by how strongly it signals "the primary official trailer". TMDb's
    // `official` flag is community-set and is frequently missing on the real studio upload,
    // so a name that literally says "Official ..." is trusted too. Accessibility / alt-format
    // cuts (see DemotedMarkerPattern) are real official uploads but not the primary trailer,
    // so a marked video always ranks below an unmarked one of the same flag/name strength.
    // higher rank wins; ties break on newest (PublishedAt). pins #111.
    private static int OfficialRank(TrailerDto v)
    {
        bool namedOfficial = v.Name.Contains("official", StringComparison.OrdinalIgnoreCase);
        bool marked = DemotedMarkerPattern().IsMatch(v.Name);
        return (v.Official, namedOfficial, marked) switch
        {
            (true, _, false) => 4,      // flagged official, primary cut — the canonical studio trailer
            (false, true, false) => 3,  // unflagged but named "Official ..." — trust the name over the flag
            (true, _, true) => 2,       // flagged official, but an alt cut (ASL / audio-described / ...)
            (false, true, true) => 1,   // named official, but an alt cut
            _ => 0,                      // not official by flag or name
        };
    }
}
