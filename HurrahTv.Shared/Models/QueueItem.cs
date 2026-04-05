namespace HurrahTv.Shared.Models;

public class QueueItem
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "";
    public string Title { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public int Position { get; set; }
    public QueueStatus Status { get; set; } = QueueStatus.WantToWatch;
    public string AvailableOnJson { get; set; } = "[]"; // service provider IDs
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // watchlist fields
    public int? Rating { get; set; } // 1-5 stars
    public int? LastSeasonWatched { get; set; }
    public int? LastEpisodeWatched { get; set; }
    public DateTime? LatestEpisodeDate { get; set; } // last aired episode
    public DateTime? NextEpisodeDate { get; set; } // next upcoming episode
    public DateTime? LastEpisodeCheckAt { get; set; } // when we last refreshed from TMDb

    public string PosterUrl(string size = "w342") => TmdbImage.Url(PosterPath, size);

    public bool HasNewEpisode => LatestEpisodeDate.HasValue
        && LatestEpisodeDate.Value >= DateTime.UtcNow.AddDays(-7);

    public bool HasUpcomingEpisode => NextEpisodeDate.HasValue
        && NextEpisodeDate.Value <= DateTime.UtcNow.AddDays(7)
        && NextEpisodeDate.Value > DateTime.UtcNow;

    // latest episode aired within the last 30 days
    public bool HasEpisodeThisMonth => LatestEpisodeDate.HasValue
        && LatestEpisodeDate.Value >= DateTime.UtcNow.AddDays(-30);

    // upcoming episode within next 7 days
    public bool HasUpcomingThisWeek => NextEpisodeDate.HasValue
        && NextEpisodeDate.Value <= DateTime.UtcNow.AddDays(7)
        && NextEpisodeDate.Value > DateTime.UtcNow;
}

public enum QueueStatus
{
    WantToWatch = 0,  // was "Queued" — bookmarked for later
    Watching = 1,     // actively watching
    Finished = 2,     // "Watched" in UI — seen it, not necessarily completed
    Liked = 3,        // finished and loved — feeds recommendations
    NotForMe = 4      // negative signal (replaces dismissals over time)
}
