namespace HurrahTv.Shared.Models;

public class QueueItem
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public string MediaType { get; set; } = "";
    public string Title { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public int Position { get; set; }
    public QueueStatus Status { get; set; } = QueueStatus.Queued;
    public string AvailableOnJson { get; set; } = "[]"; // service provider IDs
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public string PosterUrl(string size = "w342") => TmdbImage.Url(PosterPath, size);
}

public enum QueueStatus
{
    Queued = 0,
    Watching = 1,
    Watched = 2
}
