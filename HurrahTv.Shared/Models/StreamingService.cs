namespace HurrahTv.Shared.Models;

public class StreamingService
{
    public int TmdbProviderId { get; set; }
    public string Name { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Color { get; set; } = "";

    public string LogoUrl(string size = "w92") => TmdbImage.Url(LogoPath, size);

    // well-known provider IDs from TMDb. LogoPath values copied from TMDb /watch/providers
    // so QueueItem surfaces (which only carry provider IDs) can resolve a logo without
    // an extra API call. If TMDb rotates a path, the logo renders as empty — not a crash.
    public static readonly StreamingService[] All =
    [
        new() { TmdbProviderId = 8,    Name = "Netflix",             LogoPath = "/rK1KljqmbvO9HQa1PBFLILWah72.png", Color = "#E50914" },
        new() { TmdbProviderId = 9,    Name = "Amazon Prime Video",  LogoPath = "/gMZdpavHmxFNnLpMHwVxfqeux2g.png", Color = "#00A8E1" },
        new() { TmdbProviderId = 15,   Name = "Hulu",                LogoPath = "/44uAnmSqvA4yBOdbPWN8YgQHjWm.png", Color = "#1CE783" },
        new() { TmdbProviderId = 337,  Name = "Disney+",             LogoPath = "/5eZ872CghnHFLB1j8grszbrx0dx.png",  Color = "#113CCF" },
        new() { TmdbProviderId = 2303, Name = "Paramount+",          LogoPath = "/4N4BMd0Mm0kHAmF7RZgL5lW3cwc.png", Color = "#0064FF" },
        new() { TmdbProviderId = 386,  Name = "Peacock",             LogoPath = "/a1UIdq5BrkcAxnxcUhFsNbXnxeu.png", Color = "#000000" },
        new() { TmdbProviderId = 1899, Name = "Max",                 LogoPath = "/skypuy7SXuugIQeYg0IglmzoKaS.png", Color = "#002BE7" },
        new() { TmdbProviderId = 350,  Name = "Apple TV+",           LogoPath = "/9icYBfYFcwgCbky5VdGUIKJ4C5i.png", Color = "#555555" },
    ];

    public static readonly Dictionary<int, StreamingService> ById =
        All.ToDictionary(s => s.TmdbProviderId);

    public static string LookupLogoUrl(int providerId, string size = "w92") =>
        ById.TryGetValue(providerId, out StreamingService? svc) ? svc.LogoUrl(size) : "";

    public static string LookupName(int providerId) =>
        ById.TryGetValue(providerId, out StreamingService? svc) ? svc.Name : "";
}
