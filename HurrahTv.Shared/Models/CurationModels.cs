namespace HurrahTv.Shared.Models;

public class CuratedItem
{
    public SearchResult Result { get; set; } = null!;
    public string Reason { get; set; } = "";
}

public class CuratedRowResponse
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<SearchResult> Results { get; set; } = [];
    public Dictionary<int, string> Reasons { get; set; } = []; // TmdbId → why this user would like it
}

public class ShowMatchResult
{
    public string Match { get; set; } = ""; // "strong", "good", "stretch", "miss"
    public string Reason { get; set; } = "";
}

public class CurationResponse
{
    public List<CuratedRowResponse> Rows { get; set; } = [];
    public bool FromCache { get; set; }
    public bool WatchlistChanged { get; set; }
    public bool AiEnabled { get; set; }
    public string? Error { get; set; }
    public int CandidateCount { get; set; }
}
