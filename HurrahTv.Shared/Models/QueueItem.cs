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

    // new episode aired this calendar week (Monday–Sunday)
    public bool HasEpisodeThisWeek
    {
        get
        {
            if (!LatestEpisodeDate.HasValue) return false;
            DateTime today = DateTime.UtcNow.Date;
            int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7; // Monday=0
            DateTime monday = today.AddDays(-daysSinceMonday);
            DateTime sunday = monday.AddDays(7);
            return LatestEpisodeDate.Value.Date >= monday && LatestEpisodeDate.Value.Date < sunday;
        }
    }

    // upcoming episode within next 3 days
    public bool HasUpcomingSoon => NextEpisodeDate.HasValue
        && NextEpisodeDate.Value <= DateTime.UtcNow.AddDays(3)
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
