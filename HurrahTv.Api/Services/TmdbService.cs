using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using HurrahTv.Shared.Filters;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class TmdbService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly string _apiKey;

    // a TV show whose latest episode aired within this window is treated as "actively airing":
    // GetEpisodeDatesAsync re-derives latest/next from live season data to beat TMDb's
    // last_episode_to_air ingestion lag (#189). Dormant back-catalog skips the extra fetch.
    private static readonly TimeSpan SeasonScanActiveWindow = TimeSpan.FromDays(10);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TmdbService(HttpClient http, IMemoryCache cache, IConfiguration config)
    {
        _http = http;
        _cache = cache;
        _apiKey = config["Tmdb:ApiKey"] ?? throw new InvalidOperationException("Tmdb:ApiKey not configured");
        _http.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<List<SearchResult>> SearchAsync(string query)
    {
        string cacheKey = $"search:{query.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        // fetch page 1, then conditionally fetch more if TMDb has additional pages
        TmdbPagedResponse<TmdbMultiResult>? firstPage = await FetchSearchPageAsync(query, 1);
        if (firstPage == null) return [];

        List<SearchResult> results = [.. ExtractResults(firstPage)];

        if (firstPage.TotalPages > 1)
        {
            int extraPages = Math.Min(2, firstPage.TotalPages - 1);
            TmdbPagedResponse<TmdbMultiResult>?[] pages = [.. await Task.WhenAll(Enumerable.Range(2, extraPages)
                .Select(p => FetchSearchPageAsync(query, p)))];

            results.AddRange(pages.Where(p => p != null).SelectMany(p => ExtractResults(p!)));
            results = [.. results.DistinctBy(r => r.TmdbId)];
        }

        _cache.Set(cacheKey, results, TimeSpan.FromMinutes(30));
        return results;
    }

    private async Task<TmdbPagedResponse<TmdbMultiResult>?> FetchSearchPageAsync(string query, int page)
    {
        string url = $"search/multi?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&page={page}&language=en-US";
        return await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
    }

    private static List<SearchResult> ExtractResults(TmdbPagedResponse<TmdbMultiResult> response) =>
        [.. response.Results
            .Where(r => r.MediaType is MediaTypes.Movie or MediaTypes.Tv)
            .Select(MapToSearchResult)];

    public async Task<List<SearchResult>> TrendingAsync(string mediaType = "all", string timeWindow = "week")
    {
        string cacheKey = $"trending:{mediaType}:{timeWindow}";
        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        string url = $"trending/{mediaType}/{timeWindow}?api_key={_apiKey}&language=en-US";
        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results
            .Where(r => r.MediaType is MediaTypes.Movie or MediaTypes.Tv)
            .Select(MapToSearchResult)];

        _cache.Set(cacheKey, results, TimeSpan.FromHours(1));
        return results;
    }

    private const int NewContentDaysBack = 60;
    private const int ResultsPerProvider = 5;
    private const int DeepResultsPerProvider = 15; // for AI curation — larger pool

    // "highly-rated back catalog" discover band (AI curation deep cuts, #135)
    private const string HighlyRatedMinVoteAverage = "7.3";
    private const int HighlyRatedMinVoteCount = 200;       // floor so a few 10/10 votes can't dominate
    private const int HighlyRatedOlderThanDays = 365;      // genuinely back-catalog; New band covers recent

    // popular content on user's services (no date filter — best for "trending")
    public async Task<List<SearchResult>> PopularOnServicesAsync(List<int> providerIds, string mediaType = "tv",
        List<int>? genreIds = null, bool deep = false, bool englishOnly = false, CancellationToken cancellationToken = default) =>
        await InterleaveByProviderAsync(providerIds, mediaType, genreIds, recentOnly: false, deep: deep, englishOnly: englishOnly, cancellationToken: cancellationToken);

    // recently released content on user's services (date-filtered — "new this season")
    public async Task<List<SearchResult>> NewOnServicesAsync(List<int> providerIds, string mediaType = "tv",
        List<int>? genreIds = null, bool deep = false, bool englishOnly = false, CancellationToken cancellationToken = default) =>
        await InterleaveByProviderAsync(providerIds, mediaType, genreIds, recentOnly: true, deep: deep, englishOnly: englishOnly, cancellationToken: cancellationToken);

    // highly-rated back catalog on user's services — the "deep cuts" the New and Popular
    // bands miss. Gives AI curation non-recent, well-reviewed material so the rotating
    // Home hero isn't always the latest hit. pins #135.
    public async Task<List<SearchResult>> HighlyRatedOnServicesAsync(List<int> providerIds, string mediaType = "tv",
        List<int>? genreIds = null, bool deep = false, bool englishOnly = false, CancellationToken cancellationToken = default)
    {
        if (providerIds.Count == 0) return [];

        Task<List<SearchResult>>[] tasks = [.. providerIds.Select(pid => DiscoverHighlyRatedForProviderAsync(pid, mediaType, genreIds, englishOnly, cancellationToken))];
        await Task.WhenAll(tasks);
        return Interleave([.. tasks.Select(t => t.Result)], deep ? DeepResultsPerProvider : ResultsPerProvider);
    }

    private async Task<List<SearchResult>> InterleaveByProviderAsync(List<int> providerIds, string mediaType,
        List<int>? genreIds, bool recentOnly, bool deep = false, bool englishOnly = false, CancellationToken cancellationToken = default)
    {
        if (providerIds.Count == 0) return [];

        Task<List<SearchResult>>[] tasks = [.. providerIds.Select(pid => DiscoverForProviderAsync(pid, mediaType, genreIds, recentOnly, englishOnly, cancellationToken))];
        await Task.WhenAll(tasks);
        return Interleave([.. tasks.Select(t => t.Result)], deep ? DeepResultsPerProvider : ResultsPerProvider);
    }

    // round-robin per-provider result lists so no single large service (Netflix/Hulu)
    // dominates the combined pool. see Learnings/provider-popularity-dominance.md
    private static List<SearchResult> Interleave(IReadOnlyList<List<SearchResult>> perProvider, int take)
    {
        List<SearchResult> interleaved = [];
        HashSet<int> seen = [];
        for (int i = 0; i < take; i++)
        {
            foreach (List<SearchResult> results in perProvider)
            {
                if (i < results.Count && seen.Add(results[i].TmdbId))
                    interleaved.Add(results[i]);
            }
        }
        return interleaved;
    }

    private Task<List<SearchResult>> DiscoverForProviderAsync(int providerId, string mediaType,
        List<int>? genreIds, bool recentOnly, bool englishOnly = false, CancellationToken cancellationToken = default)
    {
        string genres = genreIds?.Count > 0 ? string.Join("|", genreIds) : "";
        string dateSuffix = recentOnly ? $":{DateTime.UtcNow.AddDays(-NewContentDaysBack):yyyy-MM-dd}" : ":all";
        string langSuffix = englishOnly ? ":en" : "";
        string cacheKey = $"discover-provider:{providerId}:{mediaType}:{genres}{dateSuffix}{langSuffix}";

        string extra = "&sort_by=popularity.desc";
        if (recentOnly)
        {
            string dateParam = mediaType == MediaTypes.Tv ? "first_air_date" : "primary_release_date";
            extra += $"&{dateParam}.gte={DateTime.UtcNow.AddDays(-NewContentDaysBack):yyyy-MM-dd}&{dateParam}.lte={DateTime.UtcNow:yyyy-MM-dd}";
        }
        extra += GenreAndLangParams(genres, englishOnly);

        return DiscoverAsync(providerId, mediaType, cacheKey, extra, TimeSpan.FromHours(2), cancellationToken);
    }

    private Task<List<SearchResult>> DiscoverHighlyRatedForProviderAsync(int providerId, string mediaType,
        List<int>? genreIds, bool englishOnly, CancellationToken cancellationToken)
    {
        string genres = genreIds?.Count > 0 ? string.Join("|", genreIds) : "";
        string langSuffix = englishOnly ? ":en" : "";
        string cacheKey = $"discover-toprated:{providerId}:{mediaType}:{genres}{langSuffix}";

        // sort by rating, gate on a vote-count floor so a handful of perfect scores on an obscure
        // title can't top the list, and cap the release date so these are genuinely back-catalog
        // (the New band already covers the last 60 days).
        string dateParam = mediaType == MediaTypes.Tv ? "first_air_date" : "primary_release_date";
        string extra = $"&sort_by=vote_average.desc&vote_count.gte={HighlyRatedMinVoteCount}&vote_average.gte={HighlyRatedMinVoteAverage}" +
                       $"&{dateParam}.lte={DateTime.UtcNow.AddDays(-HighlyRatedOlderThanDays):yyyy-MM-dd}" +
                       GenreAndLangParams(genres, englishOnly);

        return DiscoverAsync(providerId, mediaType, cacheKey, extra, TimeSpan.FromHours(12), cancellationToken);  // back catalog changes slowly
    }

    private static string GenreAndLangParams(string genres, bool englishOnly)
    {
        string p = "";
        if (!string.IsNullOrEmpty(genres)) p += $"&with_genres={genres}";   // TMDb OR-separated, literal |
        if (englishOnly) p += "&with_original_language=en";
        return p;
    }

    // shared discover scaffolding: the per-provider/flatrate/region base + fetch + map + cache.
    // Each band (New/Popular/HighlyRated) supplies its own sort/date/vote params, cache key, and TTL.
    private async Task<List<SearchResult>> DiscoverAsync(int providerId, string mediaType, string cacheKey,
        string extraParams, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        // TMDb requires literal | as OR separator — do not URL-encode
        string url = $"discover/{mediaType}?api_key={_apiKey}" +
                     $"&with_watch_providers={providerId}" +
                     $"&with_watch_monetization_types=flatrate|ads" +
                     $"&watch_region=US&language=en-US" +
                     extraParams;

        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url, cancellationToken);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results.Select(r => { r.MediaType = mediaType; return MapToSearchResult(r); })];

        _cache.Set(cacheKey, results, cacheTtl);
        return results;
    }

    // keep all results, show user's service badges, flag items not on their services
    public async Task<List<SearchResult>> EnrichUserServicesOnlyAsync(List<SearchResult> results, List<int> providerIds, bool flagOtherServices = false)
    {
        if (results.Count == 0) return [];

        HashSet<int> userProviders = [.. providerIds];

        Task<(SearchResult result, List<AvailableService> providers)>[] tasks = [.. results
            .Select(async r =>
            {
                List<AvailableService> providers = await GetWatchProvidersAsync(r.TmdbId, r.MediaType);
                return (r, providers);
            })];

        (SearchResult result, List<AvailableService> providers)[] enriched = await Task.WhenAll(tasks);

        foreach ((SearchResult result, List<AvailableService> providers) in enriched)
        {
            List<AvailableService> streaming = [.. providers.Where(p => p.Type is ProviderType.Flatrate or ProviderType.Ads)];
            List<AvailableService> userMatches = [.. streaming.Where(p => userProviders.Contains(p.ProviderId))];
            result.AvailableOn = userMatches;

            if (flagOtherServices && userMatches.Count == 0 && streaming.Count > 0)
                result.NotOnYourServices = true;
            if (flagOtherServices && userMatches.Count == 0 && streaming.Count == 0)
                result.NoStreamingInfo = true;
        }

        return [.. enriched.Select(e => e.result)];
    }

    public async Task<List<SearchResult>> FilterToUserServicesAsync(List<SearchResult> results, List<int> providerIds, CancellationToken cancellationToken = default)
    {
        if (providerIds.Count == 0) return [];

        HashSet<int> userProviders = [.. providerIds];

        Task<(SearchResult result, List<AvailableService> providers)>[] tasks = [.. results
            .Select(async r =>
            {
                List<AvailableService> providers = await GetWatchProvidersAsync(r.TmdbId, r.MediaType, cancellationToken);
                return (r, providers);
            })];

        (SearchResult result, List<AvailableService> providers)[] enriched = await Task.WhenAll(tasks);

        List<SearchResult> filtered = [];
        foreach ((SearchResult? result, List<AvailableService>? providers) in enriched)
        {
            List<AvailableService> matching = [.. providers.Where(p => (p.Type is ProviderType.Flatrate or ProviderType.Ads) && userProviders.Contains(p.ProviderId))];

            if (matching.Count > 0)
            {
                result.AvailableOn = matching;
                filtered.Add(result);
            }
        }

        return filtered;
    }

    public async Task<ShowDetails?> GetDetailsAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"details:{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out ShowDetails? cached))
            return cached!.Clone(); // hand callers a fresh copy — see ShowDetails.Clone (#109)

        string url = $"{mediaType}/{tmdbId}?api_key={_apiKey}&append_to_response=watch/providers&language=en-US";
        JsonElement? raw = await GetAsync<JsonElement?>(url, cancellationToken);
        if (raw == null) return null;

        JsonElement json = raw.Value;
        ShowDetails details = new()
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = json.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? "" :
                    json.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
            Overview = json.GetProperty("overview").GetString() ?? "",
            PosterPath = json.TryGetProperty("poster_path", out JsonElement pp) ? pp.GetString() ?? "" : "",
            BackdropPath = json.TryGetProperty("backdrop_path", out JsonElement bp) ? bp.GetString() ?? "" : "",
            Tagline = json.TryGetProperty("tagline", out JsonElement tl) ? tl.GetString() ?? "" : "",
            VoteAverage = json.TryGetProperty("vote_average", out JsonElement va) ? va.GetDouble() : 0,
            Status = json.TryGetProperty("status", out JsonElement st) ? st.GetString() ?? "" : "",
        };

        if (mediaType == "tv")
        {
            details.FirstAirDate = json.TryGetProperty("first_air_date", out JsonElement fad) ? fad.GetString() : null;
            details.NumberOfSeasons = json.TryGetProperty("number_of_seasons", out JsonElement nos) ? nos.GetInt32() : null;
            details.NumberOfEpisodes = json.TryGetProperty("number_of_episodes", out JsonElement noe) ? noe.GetInt32() : null;

            if (json.TryGetProperty("seasons", out JsonElement seasons))
            {
                foreach (JsonElement s in seasons.EnumerateArray())
                {
                    details.Seasons.Add(new SeasonInfo
                    {
                        SeasonNumber = s.GetProperty("season_number").GetInt32(),
                        Name = s.GetProperty("name").GetString() ?? "",
                        EpisodeCount = s.GetProperty("episode_count").GetInt32(),
                        AirDate = s.TryGetProperty("air_date", out JsonElement ad) ? ad.GetString() : null,
                        PosterPath = s.TryGetProperty("poster_path", out JsonElement sp) ? sp.GetString() ?? "" : "",
                    });
                }
            }

            // last aired episode
            if (json.TryGetProperty("last_episode_to_air", out JsonElement lastEp) && lastEp.ValueKind != JsonValueKind.Null)
            {
                details.LastEpisodeAirDate = lastEp.TryGetProperty("air_date", out JsonElement laDate) ? laDate.GetString() : null;
                details.LastEpisodeName = lastEp.TryGetProperty("name", out JsonElement laName) ? laName.GetString() : null;
                details.LastEpisodeSeason = lastEp.TryGetProperty("season_number", out JsonElement laSeason) ? laSeason.GetInt32() : null;
                details.LastEpisodeNumber = lastEp.TryGetProperty("episode_number", out JsonElement laEp) ? laEp.GetInt32() : null;
            }

            // next upcoming episode
            if (json.TryGetProperty("next_episode_to_air", out JsonElement nextEp) && nextEp.ValueKind != JsonValueKind.Null)
            {
                details.NextEpisodeAirDate = nextEp.TryGetProperty("air_date", out JsonElement naDate) ? naDate.GetString() : null;
                details.NextEpisodeName = nextEp.TryGetProperty("name", out JsonElement naName) ? naName.GetString() : null;
                details.NextEpisodeSeason = nextEp.TryGetProperty("season_number", out JsonElement naSeason) ? naSeason.GetInt32() : null;
                details.NextEpisodeNumber = nextEp.TryGetProperty("episode_number", out JsonElement naEp) ? naEp.GetInt32() : null;
            }
        }
        else
        {
            details.ReleaseDate = json.TryGetProperty("release_date", out JsonElement rd) ? rd.GetString() : null;
            details.Runtime = json.TryGetProperty("runtime", out JsonElement rt) ? rt.GetInt32() : null;
        }

        if (json.TryGetProperty("genres", out JsonElement genres))
        {
            foreach (JsonElement g in genres.EnumerateArray())
                details.Genres.Add(g.GetProperty("name").GetString() ?? "");
        }

        if (json.TryGetProperty("watch/providers", out JsonElement wp) &&
            wp.TryGetProperty("results", out JsonElement wpResults) &&
            wpResults.TryGetProperty("US", out JsonElement us))
        {
            details.AvailableOn = ParseProviders(us);
        }

        _cache.Set(cacheKey, details, TimeSpan.FromHours(6));
        return details.Clone();
    }

    // separate from GetDetailsAsync so curation fan-out (~20 TMDb IDs per row hydration)
    // doesn't pay for the videos block it never reads. DetailsEndpoints parallel-fetches
    // this alongside GetDetailsAsync so the user-perceived round-trip stays single. (#110)
    public async Task<List<TrailerDto>> GetTrailersAsync(int tmdbId, string mediaType)
    {
        string cacheKey = $"trailers:{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out List<TrailerDto>? cached))
            return [.. cached!]; // defensive copy — same rationale as ShowDetails.Clone (#109)

        string url = $"{mediaType}/{tmdbId}/videos?api_key={_apiKey}&language=en-US";
        JsonElement? raw = await GetAsync<JsonElement?>(url);
        if (raw == null) return [];

        List<TrailerDto> trailers = [];
        if (raw.Value.TryGetProperty("results", out JsonElement videoResults) &&
            videoResults.ValueKind == JsonValueKind.Array)
        {
            trailers = TrailerFilters.PickTop(ParseTrailers(videoResults));
        }

        _cache.Set(cacheKey, trailers, TimeSpan.FromHours(6));
        return [.. trailers];
    }

    public async Task<List<AvailableService>> GetWatchProvidersAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"providers:{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out List<AvailableService>? cached))
            return [.. cached!]; // defensive copy — same rationale as ShowDetails.Clone (#109)

        string url = $"{mediaType}/{tmdbId}/watch/providers?api_key={_apiKey}";
        JsonElement? raw = await GetAsync<JsonElement?>(url, cancellationToken);
        if (raw == null) return [];

        List<AvailableService> providers = [];
        if (raw.Value.TryGetProperty("results", out JsonElement results) &&
            results.TryGetProperty("US", out JsonElement us))
        {
            providers = ParseProviders(us);
        }

        _cache.Set(cacheKey, providers, TimeSpan.FromHours(12));
        return [.. providers]; // copy so caller mutation can't reach the cached list (#109)
    }

    private static List<TrailerDto> ParseTrailers(JsonElement videoResults)
    {
        List<TrailerDto> trailers = [];
        foreach (JsonElement v in videoResults.EnumerateArray())
        {
            DateTime? publishedAt = null;
            // invariant culture + assume/adjust universal so the sort is deterministic
            // regardless of host timezone or culture — TMDb sends ISO-8601 'Z' timestamps
            if (v.TryGetProperty("published_at", out JsonElement pa) && pa.ValueKind == JsonValueKind.String
                && DateTime.TryParse(pa.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            {
                publishedAt = parsed;
            }

            trailers.Add(new TrailerDto
            {
                Key = v.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? "" : "",
                Name = v.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                Site = v.TryGetProperty("site", out JsonElement s) ? s.GetString() ?? "" : "",
                Type = v.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "",
                Official = v.TryGetProperty("official", out JsonElement off) && off.ValueKind == JsonValueKind.True,
                PublishedAt = publishedAt
            });
        }
        return trailers;
    }

    private static List<AvailableService> ParseProviders(JsonElement usData)
    {
        List<AvailableService> providers = [];
        string[] types = ProviderType.All;

        // region-level JustWatch landing link for the title — shared by every provider (#140).
        // only accept https:// — the client renders this straight into an href, and Blazor does
        // not block non-http(s) schemes (javascript:, data:) in attributes. drop anything else.
        string link = usData.TryGetProperty("link", out JsonElement lk) ? lk.GetString() ?? "" : "";
        if (!link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) link = "";

        foreach (string type in types)
        {
            if (!usData.TryGetProperty(type, out JsonElement arr)) continue;
            foreach (JsonElement p in arr.EnumerateArray())
            {
                int id = p.GetProperty("provider_id").GetInt32();
                // avoid duplicates (same provider in multiple categories)
                if (providers.Any(x => x.ProviderId == id)) continue;

                providers.Add(new AvailableService
                {
                    ProviderId = id,
                    ProviderName = p.GetProperty("provider_name").GetString() ?? "",
                    LogoPath = p.TryGetProperty("logo_path", out JsonElement lp) ? lp.GetString() ?? "" : "",
                    Type = type,
                    Link = link
                });
            }
        }

        return providers;
    }

    // recommendations based on a specific title
    public async Task<List<SearchResult>> GetRecommendationsAsync(int tmdbId, string mediaType)
    {
        string cacheKey = $"recs:{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        string url = $"{mediaType}/{tmdbId}/recommendations?api_key={_apiKey}&language=en-US&page=1";
        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results
            .Where(r => (r.MediaType ?? mediaType) is MediaTypes.Movie or MediaTypes.Tv)
            .Select(r => { r.MediaType ??= mediaType; return MapToSearchResult(r); })];

        _cache.Set(cacheKey, results, TimeSpan.FromHours(6));
        return results;
    }

    // returns dates + season/episode numbers for the latest aired and next upcoming episodes
    public async Task<(DateTime? LastAired, int? LastSeason, int? LastEpisode, DateTime? NextAir, int? NextSeason, int? NextEpisode)> GetEpisodeDatesAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"episode-dates:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out (DateTime? LastAired, int? LastSeason, int? LastEpisode, DateTime? NextAir, int? NextSeason, int? NextEpisode) cached))
            return cached;

        string url = $"tv/{tmdbId}?api_key={_apiKey}&language=en-US";
        JsonElement? raw = await GetAsync<JsonElement?>(url, cancellationToken);
        if (raw == null) return (null, null, null, null, null, null);

        JsonElement json = raw.Value;

        DateTime? lastAired = null;
        int? lastSeason = null, lastEpisode = null;
        if (json.TryGetProperty("last_episode_to_air", out JsonElement lastEp) && lastEp.ValueKind != JsonValueKind.Null)
        {
            // air_date is date-only; parse as midnight UTC so it stores + round-trips with
            // Kind=Utc. The Home watchlist filter compares LatestEpisodeDate.Date against
            // todayUtc.Date — an Unspecified-Kind value could drift a calendar day near
            // midnight UTC. pins the date-only convention in tmdb-air-date-is-date-only.
            if (lastEp.TryGetProperty("air_date", out JsonElement lastDate) && lastDate.ValueKind == JsonValueKind.String
                && TmdbDate.TryParse(lastDate.GetString(), out DateTime parsedLast))
            {
                lastAired = parsedLast;
            }

            if (lastEp.TryGetProperty("season_number", out JsonElement lastSn) && lastSn.ValueKind == JsonValueKind.Number)
                lastSeason = lastSn.GetInt32();

            if (lastEp.TryGetProperty("episode_number", out JsonElement lastEn) && lastEn.ValueKind == JsonValueKind.Number)
                lastEpisode = lastEn.GetInt32();
        }

        DateTime? nextAir = null;
        int? nextSeason = null, nextEpisode = null;
        if (json.TryGetProperty("next_episode_to_air", out JsonElement nextEp) && nextEp.ValueKind != JsonValueKind.Null)
        {
            // same date-only → midnight UTC convention as last_episode_to_air above; the filter
            // also compares NextEpisodeDate.Date against todayUtc.Date (#170 override).
            if (nextEp.TryGetProperty("air_date", out JsonElement nextDate) && nextDate.ValueKind == JsonValueKind.String
                && TmdbDate.TryParse(nextDate.GetString(), out DateTime parsedNext))
            {
                nextAir = parsedNext;
            }

            if (nextEp.TryGetProperty("season_number", out JsonElement nextSn) && nextSn.ValueKind == JsonValueKind.Number)
                nextSeason = nextSn.GetInt32();

            if (nextEp.TryGetProperty("episode_number", out JsonElement nextEn) && nextEn.ValueKind == JsonValueKind.Number)
                nextEpisode = nextEn.GetInt32();
        }

        // TMDb's last_episode_to_air can lag the live season data the Details episode browser
        // reads (GetSeasonAsync) — for a daily show that produced a stale "X days ago" on Home
        // vs. a newer episode shown in Details (#189). For an actively-airing show, re-derive
        // latest/next from the season's episodes so the stored values match Details. Gated on
        // recency so we only pay the extra (6h-cached) season fetch for shows actually airing.
        DateTime today = DateTime.UtcNow.Date;
        if (lastAired is { } recentlyAired && lastSeason is { } season
            && recentlyAired.Date >= today - SeasonScanActiveWindow)
        {
            SeasonDetail? seasonDetail = await GetSeasonAsync(tmdbId, season, cancellationToken);
            if (seasonDetail is not null)
            {
                (DateTime? freshLatest, int? freshLatestEp, DateTime? freshNext, int? freshNextEp)
                    = PickFreshestFromSeason(seasonDetail, today);

                // override only when the season scan found something genuinely fresher than the
                // show-endpoint values — never regress to older data on a thin/partial payload.
                // Compare (date, episode), not date alone: when last_episode_to_air lags by
                // episode number on the SAME day (a show airing twice in a day, the exact
                // ingestion-lag case this guards), the higher-numbered season episode still wins.
                if (freshLatest is { } sld && freshLatestEp is { } sEp
                    && (sld.Date, sEp).CompareTo((lastAired.Value.Date, lastEpisode ?? -1)) > 0)
                {
                    lastAired = sld;
                    lastSeason = season;
                    lastEpisode = sEp;
                }
                // symmetric for next: a sooner date — or the same date with a lower episode
                // number — is the more-correct "next" than next_episode_to_air.
                if (freshNext is { } snd && freshNextEp is { } nEp
                    && (nextAir is null
                        || (snd.Date, nEp).CompareTo((nextAir.Value.Date, nextEpisode ?? int.MaxValue)) < 0))
                {
                    nextAir = snd;
                    nextSeason = season;
                    nextEpisode = nEp;
                }
            }
        }

        (DateTime? lastAired, int? lastSeason, int? lastEpisode, DateTime? nextAir, int? nextSeason, int? nextEpisode) result = (lastAired, lastSeason, lastEpisode, nextAir, nextSeason, nextEpisode);
        _cache.Set(cacheKey, result, TimeSpan.FromHours(6));
        return result;
    }

    public async Task<SeasonDetail?> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"season:{tmdbId}:{seasonNumber}";
        if (_cache.TryGetValue(cacheKey, out SeasonDetail? cached))
            return cached!.Clone(); // defensive copy — same rationale as ShowDetails.Clone (#109)

        string url = $"tv/{tmdbId}/season/{seasonNumber}?api_key={_apiKey}&language=en-US";
        JsonElement? raw = await GetAsync<JsonElement?>(url, cancellationToken);
        if (raw == null) return null;

        JsonElement json = raw.Value;
        SeasonDetail detail = new()
        {
            SeasonNumber = seasonNumber,
            Name = json.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : ""
        };

        if (json.TryGetProperty("episodes", out JsonElement episodes))
        {
            foreach (JsonElement ep in episodes.EnumerateArray())
            {
                detail.Episodes.Add(new EpisodeInfo
                {
                    EpisodeNumber = ep.GetProperty("episode_number").GetInt32(),
                    Name = ep.TryGetProperty("name", out JsonElement epName) ? epName.GetString() ?? "" : "",
                    AirDate = ep.TryGetProperty("air_date", out JsonElement airDate) && airDate.ValueKind == JsonValueKind.String ? airDate.GetString() : null,
                    Overview = ep.TryGetProperty("overview", out JsonElement overview) ? overview.GetString() ?? "" : "",
                    Runtime = ep.TryGetProperty("runtime", out JsonElement runtime) && runtime.ValueKind == JsonValueKind.Number ? runtime.GetInt32() : null,
                    StillPath = ep.TryGetProperty("still_path", out JsonElement still) && still.ValueKind == JsonValueKind.String ? still.GetString() ?? "" : ""
                });
            }
        }

        _cache.Set(cacheKey, detail, TimeSpan.FromHours(6));
        return detail.Clone();
    }

    private static SearchResult MapToSearchResult(TmdbMultiResult r) => new()
    {
        TmdbId = r.Id,
        Title = r.Title ?? r.Name ?? "",
        Overview = r.Overview ?? "",
        PosterPath = r.PosterPath ?? "",
        BackdropPath = r.BackdropPath ?? "",
        MediaType = r.MediaType ?? "",
        FirstAirDate = r.FirstAirDate,
        ReleaseDate = r.ReleaseDate,
        VoteAverage = r.VoteAverage,
        GenreIds = r.GenreIds ?? [],
        OriginalLanguage = r.OriginalLanguage ?? "",
    };

    // scans a season's episodes for the newest already-aired episode (later date wins, tie-break
    // on higher episode number) and the earliest still-upcoming one. Pure given a SeasonDetail +
    // today; the caller decides whether these beat the show-endpoint's last/next_episode_to_air.
    private static (DateTime? Latest, int? LatestEp, DateTime? Next, int? NextEp) PickFreshestFromSeason(
        SeasonDetail season, DateTime today)
    {
        DateTime? latestDate = null;
        int? latestEp = null;
        DateTime? nextDate = null;
        int? nextEp = null;

        foreach (EpisodeInfo ep in season.Episodes)
        {
            if (!TmdbDate.TryParse(ep.AirDate, out DateTime epDate)) continue;

            if (epDate.Date <= today)
            {
                if (latestDate is null || epDate.Date > latestDate.Value.Date
                    || (epDate.Date == latestDate.Value.Date && ep.EpisodeNumber > (latestEp ?? 0)))
                {
                    latestDate = epDate;
                    latestEp = ep.EpisodeNumber;
                }
            }
            // earliest still-upcoming: sooner date wins, tie-break on LOWER episode number
            // (mirror of the latest branch above) so a same-day multi-episode payload or an
            // out-of-order season array can't pick the wrong "next".
            else if (nextDate is null || epDate.Date < nextDate.Value.Date
                || (epDate.Date == nextDate.Value.Date && ep.EpisodeNumber < (nextEp ?? int.MaxValue)))
            {
                nextDate = epDate;
                nextEp = ep.EpisodeNumber;
            }
        }

        return (latestDate, latestEp, nextDate, nextEp);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        // narrow the filter to caller-driven cancellation only: HttpClient.Timeout raises
        // TaskCanceledException (a subclass of OCE) when its internal CTS fires, which
        // would otherwise escape `ex is not OperationCanceledException` and propagate
        // as 500. Same trap as Learnings/oce-rethrow-needs-token-filter.md, one layer
        // deeper. Caller cancellation rethrows; everything else (timeouts, network,
        // parse) falls into the null-return path. pins #125.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TMDb API error: {ex.Message}");
            return default;
        }
    }

    // internal TMDb response models
    private class TmdbPagedResponse<T>
    {
        public int Page { get; set; }
        public List<T> Results { get; set; } = [];
        public int TotalPages { get; set; }
        public int TotalResults { get; set; }
    }

    private class TmdbMultiResult
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Overview { get; set; }
        public string? PosterPath { get; set; }
        public string? BackdropPath { get; set; }
        public string? MediaType { get; set; }
        public string? FirstAirDate { get; set; }
        public string? ReleaseDate { get; set; }
        public double VoteAverage { get; set; }
        public List<int>? GenreIds { get; set; }
        public string? OriginalLanguage { get; set; }
    }
}
