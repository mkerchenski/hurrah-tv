namespace HurrahTv.Shared.Models;

// response shape for GET /api/queue — bundles queue items with watched episodes in one round-trip
public record QueueResponse(List<QueueItem> Items, List<WatchedEpisode> WatchedEpisodes);
