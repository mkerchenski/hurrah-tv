using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using HurrahTv.Shared.Models;

namespace HurrahTv.Api.Services;

public class TmdbService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly string _apiKey;
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

    // popular content on user's services (no date filter — best for "trending")
    public async Task<List<SearchResult>> PopularOnServicesAsync(List<int> providerIds, string mediaType = "tv",
        List<int>? genreIds = null, bool deep = false, bool englishOnly = false) =>
        await InterleaveByProviderAsync(providerIds, mediaType, genreIds, recentOnly: false, deep: deep, englishOnly: englishOnly);

    // recently released content on user's services (date-filtered — "new this season")
    public async Task<List<SearchResult>> NewOnServicesAsync(List<int> providerIds, string mediaType = "tv",
        List<int>? genreIds = null, bool deep = false, bool englishOnly = false) =>
        await InterleaveByProviderAsync(providerIds, mediaType, genreIds, recentOnly: true, deep: deep, englishOnly: englishOnly);

    private async Task<List<SearchResult>> InterleaveByProviderAsync(List<int> providerIds, string mediaType,
        List<int>? genreIds, bool recentOnly, bool deep = false, bool englishOnly = false)
    {
        if (providerIds.Count == 0) return [];

        int perProvider = deep ? DeepResultsPerProvider : ResultsPerProvider;
        Task<List<SearchResult>>[] tasks = [.. providerIds.Select(pid => DiscoverForProviderAsync(pid, mediaType, genreIds, recentOnly, englishOnly))];
        await Task.WhenAll(tasks);

        List<SearchResult> interleaved = [];
        HashSet<int> seen = [];
        for (int i = 0; i < perProvider; i++)
        {
            foreach (Task<List<SearchResult>> task in tasks)
            {
                if (i < task.Result.Count && seen.Add(task.Result[i].TmdbId))
                    interleaved.Add(task.Result[i]);
            }
        }

        return interleaved;
    }

    private async Task<List<SearchResult>> DiscoverForProviderAsync(int providerId, string mediaType,
        List<int>? genreIds, bool recentOnly, bool englishOnly = false)
    {
        string genres = genreIds?.Count > 0 ? string.Join("|", genreIds) : "";
        string dateSuffix = recentOnly ? $":{DateTime.UtcNow.AddDays(-NewContentDaysBack):yyyy-MM-dd}" : ":all";
        string langSuffix = englishOnly ? ":en" : "";
        string cacheKey = $"discover-provider:{providerId}:{mediaType}:{genres}{dateSuffix}{langSuffix}";

        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        // TMDb requires literal | as OR separator — do not URL-encode
        string url = $"discover/{mediaType}?api_key={_apiKey}" +
                     $"&with_watch_providers={providerId}" +
                     $"&with_watch_monetization_types=flatrate|ads" +
                     $"&watch_region=US&sort_by=popularity.desc&language=en-US";

        if (recentOnly)
        {
            string dateParam = mediaType == MediaTypes.Tv ? "first_air_date" : "primary_release_date";
            string dateFrom = DateTime.UtcNow.AddDays(-NewContentDaysBack).ToString("yyyy-MM-dd");
            string dateTo = DateTime.UtcNow.ToString("yyyy-MM-dd");
            url += $"&{dateParam}.gte={dateFrom}&{dateParam}.lte={dateTo}";
        }

        if (!string.IsNullOrEmpty(genres))
            url += $"&with_genres={genres}";

        if (englishOnly)
            url += "&with_original_language=en";

        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results.Select(r => { r.MediaType = mediaType; return MapToSearchResult(r); })];

        _cache.Set(cacheKey, results, TimeSpan.FromHours(2));
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

    public async Task<List<SearchResult>> FilterToUserServicesAsync(List<SearchResult> results, List<int> providerIds)
    {
        if (providerIds.Count == 0) return [];

        HashSet<int> userProviders = [.. providerIds];

        Task<(SearchResult result, List<AvailableService> providers)>[] tasks = [.. results
            .Select(async r =>
            {
                List<AvailableService> providers = await GetWatchProvidersAsync(r.TmdbId, r.MediaType);
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

    public async Task<ShowDetails?> GetDetailsAsync(int tmdbId, string mediaType)
    {
        string cacheKey = $"details:{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out ShowDetails? cached))
            return cached;

        string url = $"{mediaType}/{tmdbId}?api_key={_apiKey}&append_to_response=watch/providers&language=en-US";
        JsonElement? raw = await GetAsync<JsonElement?>(url);
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
        return details;
    }

    public async Task<List<AvailableService>> GetWatchProvidersAsync(int tmdbId, string mediaType)
    {
        string cacheKey = $"providers:{mediaType}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out List<AvailableService>? cached))
            return cached!;

        string url = $"{mediaType}/{tmdbId}/watch/providers?api_key={_apiKey}";
        JsonElement? raw = await GetAsync<JsonElement?>(url);
        if (raw == null) return [];

        List<AvailableService> providers = [];
        if (raw.Value.TryGetProperty("results", out JsonElement results) &&
            results.TryGetProperty("US", out JsonElement us))
        {
            providers = ParseProviders(us);
        }

        _cache.Set(cacheKey, providers, TimeSpan.FromHours(12));
        return providers;
    }

    private static List<AvailableService> ParseProviders(JsonElement usData)
    {
        List<AvailableService> providers = [];
        string[] types = ProviderType.All;

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
                    Type = type
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
            if (lastEp.TryGetProperty("air_date", out JsonElement lastDate) && lastDate.ValueKind == JsonValueKind.String
                && DateTime.TryParse(lastDate.GetString(), out DateTime parsedLast))
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
            if (nextEp.TryGetProperty("air_date", out JsonElement nextDate) && nextDate.ValueKind == JsonValueKind.String
                && DateTime.TryParse(nextDate.GetString(), out DateTime parsedNext))
            {
                nextAir = parsedNext;
            }

            if (nextEp.TryGetProperty("season_number", out JsonElement nextSn) && nextSn.ValueKind == JsonValueKind.Number)
                nextSeason = nextSn.GetInt32();

            if (nextEp.TryGetProperty("episode_number", out JsonElement nextEn) && nextEn.ValueKind == JsonValueKind.Number)
                nextEpisode = nextEn.GetInt32();
        }

        (DateTime? lastAired, int? lastSeason, int? lastEpisode, DateTime? nextAir, int? nextSeason, int? nextEpisode) result = (lastAired, lastSeason, lastEpisode, nextAir, nextSeason, nextEpisode);
        _cache.Set(cacheKey, result, TimeSpan.FromHours(6));
        return result;
    }

    public async Task<SeasonDetail?> GetSeasonAsync(int tmdbId, int seasonNumber)
    {
        string cacheKey = $"season:{tmdbId}:{seasonNumber}";
        if (_cache.TryGetValue(cacheKey, out SeasonDetail? cached))
            return cached;

        string url = $"tv/{tmdbId}/season/{seasonNumber}?api_key={_apiKey}&language=en-US";
        JsonElement? raw = await GetAsync<JsonElement?>(url);
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
        return detail;
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

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpResponseMessage response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
