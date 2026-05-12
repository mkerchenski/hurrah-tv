namespace HurrahTv.Shared.Models;

public record QueueStatusUpdate(QueueStatus Status);

public record PositionUpdate(int Position);

public record SentimentUpdate(int? Sentiment);

public record ProgressUpdate(int? Season, int? Episode);

public record SeenRequest(int TmdbId, string MediaType, string Title, string PosterPath, string AvailableOnJson, string BackdropPath = "");
