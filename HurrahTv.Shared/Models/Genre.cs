namespace HurrahTv.Shared.Models;

public class Genre
{
    public int TmdbId { get; set; }
    public string Name { get; set; } = "";

    // TMDb genre IDs — union of TV and movie genres
    public static readonly Genre[] All =
    [
        new() { TmdbId = 28, Name = "Action" },
        new() { TmdbId = 12, Name = "Adventure" },
        new() { TmdbId = 16, Name = "Animation" },
        new() { TmdbId = 35, Name = "Comedy" },
        new() { TmdbId = 80, Name = "Crime" },
        new() { TmdbId = 99, Name = "Documentary" },
        new() { TmdbId = 18, Name = "Drama" },
        new() { TmdbId = 10751, Name = "Family" },
        new() { TmdbId = 14, Name = "Fantasy" },
        new() { TmdbId = 36, Name = "History" },
        new() { TmdbId = 27, Name = "Horror" },
        new() { TmdbId = 10402, Name = "Music" },
        new() { TmdbId = 9648, Name = "Mystery" },
        new() { TmdbId = 10749, Name = "Romance" },
        new() { TmdbId = 878, Name = "Sci-Fi" },
        new() { TmdbId = 53, Name = "Thriller" },
        new() { TmdbId = 10752, Name = "War" },
        new() { TmdbId = 37, Name = "Western" },
    ];

    public static readonly Dictionary<int, Genre> ById =
        All.ToDictionary(g => g.TmdbId);
}
