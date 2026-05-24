namespace HurrahTv.Shared.Models;

public class ShowDetails : SearchResult
{
    public string Tagline { get; set; } = "";
    public int? NumberOfSeasons { get; set; }
    public int? NumberOfEpisodes { get; set; }
    public int? Runtime { get; set; } // minutes — for movies
    public List<string> Genres { get; set; } = [];
    public List<SeasonInfo> Seasons { get; set; } = [];
    public List<TrailerDto> Trailers { get; set; } = [];
    public string Status { get; set; } = ""; // "Returning Series", "Ended", "Released", etc.

    // latest episode info (TV only)
    public string? LastEpisodeAirDate { get; set; }
    public string? LastEpisodeName { get; set; }
    public int? LastEpisodeSeason { get; set; }
    public int? LastEpisodeNumber { get; set; }
    public string? NextEpisodeAirDate { get; set; }
    public string? NextEpisodeName { get; set; }
    public int? NextEpisodeSeason { get; set; }
    public int? NextEpisodeNumber { get; set; }

    // shallow copy with fresh list instances. TmdbService caches the canonical instance
    // and hands callers a clone so per-request mutation (e.g. user-filtered AvailableOn
    // in DetailsEndpoints) can't bleed back into the cache. pins #109.
    //
    // MemberwiseClone covers all scalar fields automatically — only mutable collections
    // need explicit re-allocation. New non-list properties added to ShowDetails or its
    // SearchResult base get cloned for free; new list properties must extend this list.
    public ShowDetails Clone()
    {
        ShowDetails copy = (ShowDetails)MemberwiseClone();
        copy.GenreIds = [.. GenreIds];
        copy.AvailableOn = [.. AvailableOn];
        copy.Genres = [.. Genres];
        copy.Seasons = [.. Seasons];
        copy.Trailers = [.. Trailers];
        return copy;
    }
}

public class TrailerDto
{
    public string Key { get; set; } = "";        // youtube video id
    public string Name { get; set; } = "";       // "Official Trailer", etc.
    public string Site { get; set; } = "";       // "YouTube" — filter discards anything else
    public string Type { get; set; } = "";       // "Trailer" — filter discards Teaser/Clip/Featurette
    public bool Official { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class SeasonInfo
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public int EpisodeCount { get; set; }
    public string? AirDate { get; set; }
    public string PosterPath { get; set; } = "";
}

public class EpisodeInfo
{
    public int EpisodeNumber { get; set; }
    public string Name { get; set; } = "";
    public string? AirDate { get; set; }
    public string Overview { get; set; } = "";
    public int? Runtime { get; set; }
    public string StillPath { get; set; } = "";
}

public class SeasonDetail
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public List<EpisodeInfo> Episodes { get; set; } = [];
}
