namespace HurrahTv.Client.Services;

// singleton service that tracks the global TV/All/Movies filter
// MainLayout renders the tabs; Home, Queue, and Search subscribe to react
public class MediaFilterService
{
    public string MediaType { get; private set; } = "all"; // "all" | "tv" | "movie"

    public event Action? OnChanged;

    public void Set(string type)
    {
        if (MediaType == type) return;
        MediaType = type;
        OnChanged?.Invoke();
    }
}
