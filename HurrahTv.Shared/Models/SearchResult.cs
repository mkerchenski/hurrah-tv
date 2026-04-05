using System.Text.Json;

namespace HurrahTv.Shared.Models;

public static class TmdbImage
{
    private const string BaseUrl = "https://image.tmdb.org/t/p/";
    public static string Url(string? path, string size = "w342") =>
        string.IsNullOrEmpty(path) ? "" : $"{BaseUrl}{size}{path}";
}

public class SearchResult
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string Overview { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public string BackdropPath { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string? FirstAirDate { get; set; }
    public string? ReleaseDate { get; set; }
    public double VoteAverage { get; set; }
    public List<int> GenreIds { get; set; } = [];
    public string OriginalLanguage { get; set; } = "";
    public List<AvailableService> AvailableOn { get; set; } = [];
    public bool NotOnYourServices { get; set; } // streaming exists but not on user's services
    public bool NoStreamingInfo { get; set; } // no provider data at all from TMDb

    public string DisplayDate => MediaType == MediaTypes.Tv ? FirstAirDate ?? "" : ReleaseDate ?? "";
    public string Year => DisplayDate.Length >= 4 ? DisplayDate[..4] : "";

    public string PosterUrl(string size = "w342") => TmdbImage.Url(PosterPath, size);
    public string BackdropUrl(string size = "w780") => TmdbImage.Url(BackdropPath, size);

    public QueueItem ToQueueItem() => new()
    {
        TmdbId = TmdbId,
        MediaType = MediaType,
        Title = Title,
        PosterPath = PosterPath,
        AvailableOnJson = JsonSerializer.Serialize(AvailableOn.Select(s => s.ProviderId).ToList()),
    };
}

public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = [];
}

public class AvailableService
{
    public int ProviderId { get; set; }
    public string ProviderName { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Type { get; set; } = "";

    public string LogoUrl(string size = "w92") => TmdbImage.Url(LogoPath, size);
}
