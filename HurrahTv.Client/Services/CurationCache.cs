using System.Text.Json;
using HurrahTv.Shared.Models;
using Microsoft.JSInterop;

namespace HurrahTv.Client.Services;

// in-memory + localStorage cache for the AI curation response. server already
// caches by watchlist hash, so the win here is mostly skipping the HTTP
// round-trip on Home re-navigation, plus surviving full page reloads via
// localStorage.
//
// invalidation: subscribes to QuickActionService item-update events so any user
// mutation (status, sentiment, mark-watched) clears the cache. otherwise stale
// recs could reference items the user just changed.
public class CurationCache : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private const string StorageKey = "hurrah:curation:v1";

    private readonly QuickActionService _quickActions;
    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _hydrateLock = new(1, 1);
    private CurationResponse? _value;
    private DateTime _storedAt;
    private bool _hydrated;

    public CurationCache(QuickActionService quickActions, IJSRuntime js)
    {
        _quickActions = quickActions;
        _js = js;
        // every successful mutation fires one of the targeted events; OnChanged only fires
        // on failure, where rehydrating from server already covers the cache's correctness
        _quickActions.OnItemUpdated += OnInvalidate;
        _quickActions.OnEpisodeWatchedChanged += OnEpisodeInvalidate;
    }

    public async Task<CurationResponse?> GetAsync()
    {
        // serialize first-time hydration so two concurrent callers don't both run
        // TryHydrateAsync (which would race on _value / _storedAt writes)
        if (!_hydrated)
        {
            await _hydrateLock.WaitAsync();
            try
            {
                if (!_hydrated)
                {
                    await TryHydrateAsync();
                    _hydrated = true;
                }
            }
            finally
            {
                _hydrateLock.Release();
            }
        }
        if (_value is null) return null;
        if (DateTime.UtcNow - _storedAt > Ttl)
        {
            _value = null;
            return null;
        }
        return _value;
    }

    public void Set(CurationResponse response)
    {
        _value = response;
        _storedAt = DateTime.UtcNow;
        _ = PersistAsync();
    }

    public void Invalidate()
    {
        _value = null;
        _ = ClearStorageAsync();
    }

    private async Task TryHydrateAsync()
    {
        try
        {
            string? json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json)) return;
            Envelope? env = JsonSerializer.Deserialize<Envelope>(json);
            if (env is null) return;
            if (DateTime.UtcNow - env.StoredAt > Ttl) return;
            _value = env.Value;
            _storedAt = env.StoredAt;
        }
        catch
        {
            // corrupted/old entry — drop and fall through to fresh fetch
            _ = ClearStorageAsync();
        }
    }

    private async Task PersistAsync()
    {
        if (_value is null) return;
        try
        {
            string json = JsonSerializer.Serialize(new Envelope(_storedAt, _value));
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // localStorage disabled / quota exceeded — in-memory cache still works
        }
    }

    private async Task ClearStorageAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch
        {
            // best-effort
        }
    }

    private void OnInvalidate(QueueItem _) => Invalidate();
    private void OnEpisodeInvalidate(int _, int _2, int _3, bool _4) => Invalidate();

    private record Envelope(DateTime StoredAt, CurationResponse Value);

    public void Dispose()
    {
        _quickActions.OnItemUpdated -= OnInvalidate;
        _quickActions.OnEpisodeWatchedChanged -= OnEpisodeInvalidate;
        _hydrateLock.Dispose();
    }
}
