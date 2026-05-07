using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

public class QuickActionService
{
    // legacy "something changed, please reload" — kept for callers that don't have item details
    public event Action? OnChanged;

    // targeted update — subscribers can splice the item into local state without a refetch
    public event Action<QueueItem>? OnItemUpdated;

    // episode-watched toggle. Tells subscribers to update their local watched-set in place.
    public event Action<int, int, int, bool>? OnEpisodeWatchedChanged;

    public event Action<QueueItem>? OnShowForQueueItem;
    public event Action<SearchResult>? OnShowForSearchResult;

    public void ShowForQueueItem(QueueItem item) => OnShowForQueueItem?.Invoke(item);
    public void ShowForSearchResult(SearchResult result) => OnShowForSearchResult?.Invoke(result);
    public void NotifyChanged() => OnChanged?.Invoke();
    public void NotifyItemUpdated(QueueItem item) => OnItemUpdated?.Invoke(item);
    public void NotifyEpisodeWatchedChanged(int tmdbId, int season, int episode, bool watched) =>
        OnEpisodeWatchedChanged?.Invoke(tmdbId, season, episode, watched);
}
