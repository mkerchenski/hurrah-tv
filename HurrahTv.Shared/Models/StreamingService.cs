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
        new() { TmdbProviderId = 8,    Name = "Netflix",             LogoPath = "/pbpMk2JmcoNnQwx5JGpXngfoWtp.jpg", Color = "#E50914" },
        new() { TmdbProviderId = 9,    Name = "Amazon Prime Video",  LogoPath = "/pvske1MyAoymrs5bguRfVqYiM9a.jpg", Color = "#00A8E1" },
        new() { TmdbProviderId = 15,   Name = "Hulu",                LogoPath = "/bxBlRPEPpMVDc4jMhSrTf2339DW.jpg", Color = "#1CE783" },
        new() { TmdbProviderId = 337,  Name = "Disney+",             LogoPath = "/97yvRBw1GzX7fXprcF80er19ot.jpg",  Color = "#113CCF" },
        new() { TmdbProviderId = 2303, Name = "Paramount+",          LogoPath = "/fts6X10Jn4QT0X6ac3udKEn2tJA.jpg", Color = "#0064FF" },
        new() { TmdbProviderId = 386,  Name = "Peacock",             LogoPath = "/2aGrp1xw3qhwCYvNGAJZPdjfeeX.jpg", Color = "#000000" },
        new() { TmdbProviderId = 1899, Name = "Max",                 LogoPath = "/jbe4gVSfRlbPTdESXhEKpornsfu.jpg", Color = "#002BE7" },
        new() { TmdbProviderId = 350,  Name = "Apple TV+",           LogoPath = "/mcbz1LgtErU9p4UdbZ0rG6RTWHX.jpg", Color = "#555555" },
    ];

    public static readonly Dictionary<int, StreamingService> ById =
        All.ToDictionary(s => s.TmdbProviderId);

    public static string LookupLogoUrl(int providerId, string size = "w92") =>
        ById.TryGetValue(providerId, out StreamingService? svc) ? svc.LogoUrl(size) : "";

    public static string LookupName(int providerId) =>
        ById.TryGetValue(providerId, out StreamingService? svc) ? svc.Name : "";
}
