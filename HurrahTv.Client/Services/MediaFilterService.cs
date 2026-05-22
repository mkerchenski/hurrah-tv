using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

// singleton service that tracks the global TV/All/Movies filter
// MainLayout renders the tabs; Home, Queue, and Search subscribe to react
// MainLayout calls InitializeAsync on first render to hydrate from UserSettings;
// Set() persists the new value back via fetch-mutate-PUT to avoid clobbering
// other settings that Home or Settings page may have written
public class MediaFilterService(ApiClient api)
{
    private Task? _loadOnce;

    public string MediaType { get; private set; } = "all"; // "all" | "tv" | "movie"

    public event Action? OnChanged;

    public Task InitializeAsync()
    {
        _loadOnce ??= LoadAsync();
        return _loadOnce;
    }

    private async Task LoadAsync()
    {
        try
        {
            UserSettings settings = await api.GetUserSettingsAsync();
            if (settings.MediaType != MediaType)
            {
                MediaType = settings.MediaType;
                OnChanged?.Invoke();
            }
        }
        catch
        {
            // best-effort hydration; default of "all" stays in place on failure
        }
    }

    // persist=false is for URL-driven changes (e.g. Queue's ?media= deep links and the
    // page-local Clear filters affordance) — the user didn't explicitly opt into changing
    // their saved global preference, so we mutate the in-memory filter without writing
    // through to UserSettings. MainLayout's nav toggle uses the default persist=true.
    public void Set(string type, bool persist = true)
    {
        if (MediaType == type) return;
        MediaType = type;
        OnChanged?.Invoke();
        if (persist) _ = PersistAsync(type);
    }

    private async Task PersistAsync(string type)
    {
        try
        {
            UserSettings settings = await api.GetUserSettingsAsync();
            settings.MediaType = type;
            await api.SetUserSettingsAsync(settings);
        }
        catch
        {
            // fire-and-forget; UI already reflects the change
        }
    }
}
