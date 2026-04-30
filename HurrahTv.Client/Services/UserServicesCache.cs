namespace HurrahTv.Client.Services;

// scoped cache of the user's active streaming service IDs. Components that render
// service-logo overlays (PosterCard, WatchlistRow, Queue) read this to gate which
// logos appear. MainLayout pre-warms on first render; Settings.Save() invalidates
// after a successful PUT.
public class UserServicesCache(ApiClient api)
{
    private Task<IReadOnlyList<int>>? _inFlight;
    private IReadOnlyList<int> _cached = [];

    public event Action? Changed;

    public Task<IReadOnlyList<int>> GetAsync()
    {
        if (_inFlight != null) return _inFlight;
        _inFlight = LoadAsync();
        return _inFlight;
    }

    // synchronous accessor for render paths. Returns empty until first load resolves;
    // subscribers to Changed re-render when the cache fills.
    public IReadOnlyList<int> TryGetCached() => _cached;

    public void Invalidate()
    {
        _cached = [];
        _inFlight = null;
        Changed?.Invoke();
    }

    public void Set(IReadOnlyList<int> services)
    {
        _cached = services;
        _inFlight = Task.FromResult(_cached);
        Changed?.Invoke();
    }

    private async Task<IReadOnlyList<int>> LoadAsync()
    {
        List<int> services = await api.GetUserServicesAsync();
        _cached = services;
        Changed?.Invoke();
        return _cached;
    }
}
