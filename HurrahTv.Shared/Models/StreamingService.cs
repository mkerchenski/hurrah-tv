namespace HurrahTv.Shared.Models;

public class StreamingService
{
    public int TmdbProviderId { get; set; }
    public string Name { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Color { get; set; } = "";

    // well-known provider IDs from TMDb
    public static readonly StreamingService[] All =
    [
        new() { TmdbProviderId = 8, Name = "Netflix", Color = "#E50914" },
        new() { TmdbProviderId = 9, Name = "Amazon Prime Video", Color = "#00A8E1" },
        new() { TmdbProviderId = 15, Name = "Hulu", Color = "#1CE783" },
        new() { TmdbProviderId = 337, Name = "Disney+", Color = "#113CCF" },
        new() { TmdbProviderId = 2303, Name = "Paramount+", Color = "#0064FF" },
        new() { TmdbProviderId = 386, Name = "Peacock", Color = "#000000" },
        new() { TmdbProviderId = 1899, Name = "Max", Color = "#002BE7" },
        new() { TmdbProviderId = 350, Name = "Apple TV+", Color = "#555555" },
    ];

    public static readonly Dictionary<int, StreamingService> ById =
        All.ToDictionary(s => s.TmdbProviderId);
}
