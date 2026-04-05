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
    public int? Sentiment { get; set; } // null=no opinion, 1=down, 2=up, 3=favorite
    public string AvailableOnJson { get; set; } = "[]"; // service provider IDs
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // watchlist fields
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
    WantToWatch = 0,  // bookmarked for later
    Watching = 1,     // actively watching
    Finished = 2,     // "Watched" in UI — seen it, not necessarily completed
    NotForMe = 4      // negative signal
}

// sentiment is orthogonal to list status — tracked separately
public static class SentimentLevel
{
    public const int Down = 1;      // thumbs down
    public const int Up = 2;        // thumbs up
    public const int Favorite = 3;  // double thumbs up / favorite

    public static bool IsValid(int? sentiment) => sentiment is null or (>= Down and <= Favorite);
}

public class SeasonSentiment
{
    public int TmdbId { get; set; }
    public int SeasonNumber { get; set; }
    public int Sentiment { get; set; }
}

public class EpisodeSentiment
{
    public int TmdbId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public int Sentiment { get; set; }
}

// all sentiments for a show (returned by GET /api/shows/{id}/sentiments)
public class ShowSentiments
{
    public List<SeasonSentiment> Seasons { get; set; } = [];
    public List<EpisodeSentiment> Episodes { get; set; } = [];
}
