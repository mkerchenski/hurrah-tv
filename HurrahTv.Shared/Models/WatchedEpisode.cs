namespace HurrahTv.Shared.Models;

public record WatchedEpisode(int TmdbId, int Season, int Episode);

// request body for POST/DELETE /api/episodes/watched
public record WatchedEpisodeRequest(int TmdbId, int Season, int Episode);
