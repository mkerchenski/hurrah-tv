namespace HurrahTv.Shared.Models;

public class SearchResult
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string Overview { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public string BackdropPath { get; set; } = "";
    public string MediaType { get; set; } = ""; // "movie" or "tv"
    public string? FirstAirDate { get; set; }
    public string? ReleaseDate { get; set; }
    public double VoteAverage { get; set; }
    public List<int> GenreIds { get; set; } = [];
    public List<AvailableService> AvailableOn { get; set; } = [];

    public string DisplayDate => MediaType == "tv" ? FirstAirDate ?? "" : ReleaseDate ?? "";
    public string Year => DisplayDate.Length >= 4 ? DisplayDate[..4] : "";

    public string PosterUrl(string size = "w342") =>
        string.IsNullOrEmpty(PosterPath) ? "" : $"https://image.tmdb.org/t/p/{size}{PosterPath}";

    public string BackdropUrl(string size = "w780") =>
        string.IsNullOrEmpty(BackdropPath) ? "" : $"https://image.tmdb.org/t/p/{size}{BackdropPath}";
}

public class AvailableService
{
    public int ProviderId { get; set; }
    public string ProviderName { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Type { get; set; } = ""; // "flatrate", "buy", "rent", "ads"

    public string LogoUrl(string size = "w92") =>
        string.IsNullOrEmpty(LogoPath) ? "" : $"https://image.tmdb.org/t/p/{size}{LogoPath}";
}
