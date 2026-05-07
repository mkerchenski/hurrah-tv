using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

// in-memory cache for the AI curation response. spans the lifetime of the WASM
// app session (cleared on full page reload). server already caches by watchlist
// hash, so we mostly just save the HTTP round-trip on Home re-navigation.
//
// invalidation: subscribes to QuickActionService item-update events so any user
// mutation (status, sentiment, mark-watched) clears the cache. otherwise stale
// recs could reference items the user just changed.
public class CurationCache : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly QuickActionService _quickActions;
    private CurationResponse? _value;
    private DateTime _storedAt;

    public CurationCache(QuickActionService quickActions)
    {
        _quickActions = quickActions;
        _quickActions.OnItemUpdated += OnInvalidate;
        _quickActions.OnEpisodeWatchedChanged += OnEpisodeInvalidate;
        _quickActions.OnChanged += Invalidate;
    }

    public CurationResponse? Get()
    {
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
    }

    public void Invalidate()
    {
        _value = null;
    }

    private void OnInvalidate(QueueItem _) => Invalidate();
    private void OnEpisodeInvalidate(int _, int _2, int _3, bool _4) => Invalidate();

    public void Dispose()
    {
        _quickActions.OnItemUpdated -= OnInvalidate;
        _quickActions.OnEpisodeWatchedChanged -= OnEpisodeInvalidate;
        _quickActions.OnChanged -= Invalidate;
    }
}
