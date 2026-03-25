namespace HurrahTv.Shared.Models;

public class ShowDetails : SearchResult
{
    public string Tagline { get; set; } = "";
    public int? NumberOfSeasons { get; set; }
    public int? NumberOfEpisodes { get; set; }
    public int? Runtime { get; set; } // minutes — for movies
    public List<string> Genres { get; set; } = [];
    public List<SeasonInfo> Seasons { get; set; } = [];
    public string Status { get; set; } = ""; // "Returning Series", "Ended", "Released", etc.
}

public class SeasonInfo
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public int EpisodeCount { get; set; }
    public string? AirDate { get; set; }
    public string PosterPath { get; set; } = "";
}
