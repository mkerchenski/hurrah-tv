using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

public class QuickActionService
{
    public event Action? OnChanged;

    public event Action<QueueItem>? OnShowForQueueItem;
    public event Action<SearchResult>? OnShowForSearchResult;

    public void ShowForQueueItem(QueueItem item) => OnShowForQueueItem?.Invoke(item);
    public void ShowForSearchResult(SearchResult result) => OnShowForSearchResult?.Invoke(result);
    public void NotifyChanged() => OnChanged?.Invoke();
}
