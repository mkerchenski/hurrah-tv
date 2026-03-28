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

    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1)
    {
        string cacheKey = $"search:{query}:{page}";
        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        string url = $"search/multi?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&page={page}&language=en-US";
        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results
            .Where(r => r.MediaType is MediaType.Movie or MediaType.Tv)
            .Select(MapToSearchResult)];

        _cache.Set(cacheKey, results, TimeSpan.FromMinutes(30));
        return results;
    }

    public async Task<List<SearchResult>> TrendingAsync(string mediaType = "all", string timeWindow = "week")
    {
        string cacheKey = $"trending:{mediaType}:{timeWindow}";
        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        string url = $"trending/{mediaType}/{timeWindow}?api_key={_apiKey}&language=en-US";
        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results
            .Where(r => r.MediaType is MediaType.Movie or MediaType.Tv)
            .Select(MapToSearchResult)];

        _cache.Set(cacheKey, results, TimeSpan.FromHours(1));
        return results;
    }

    private const int NewContentDaysBack = 60;

    public async Task<List<SearchResult>> NewOnServicesAsync(List<int> providerIds, string mediaType = "tv",
        List<int>? genreIds = null, int page = 1)
    {
        if (providerIds.Count == 0) return [];

        string providers = string.Join("|", providerIds);
        string genres = genreIds?.Count > 0 ? string.Join("|", genreIds) : "";
        string dateFrom = DateTime.UtcNow.AddDays(-NewContentDaysBack).ToString("yyyy-MM-dd");
        string dateTo = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string cacheKey = $"new-on-services:{providers}:{mediaType}:{genres}:{page}:{dateFrom}";

        if (_cache.TryGetValue(cacheKey, out List<SearchResult>? cached))
            return cached!;

        string dateParam = mediaType == MediaType.Tv ? "first_air_date" : "primary_release_date";
        string url = $"discover/{mediaType}?api_key={_apiKey}" +
                     $"&with_watch_providers={providers}" +
                     $"&with_watch_monetization_types=flatrate" +
                     $"&watch_region=US&sort_by=popularity.desc&language=en-US&page={page}" +
                     $"&{dateParam}.gte={dateFrom}&{dateParam}.lte={dateTo}";

        if (!string.IsNullOrEmpty(genres))
            url += $"&with_genres={genres}";

        TmdbPagedResponse<TmdbMultiResult>? response = await GetAsync<TmdbPagedResponse<TmdbMultiResult>>(url);
        if (response == null) return [];

        List<SearchResult> results = [.. response.Results.Select(r => { r.MediaType = mediaType; return MapToSearchResult(r); })];

        _cache.Set(cacheKey, results, TimeSpan.FromHours(2));
        return results;
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
            List<AvailableService> matching = [.. providers.Where(p => p.Type == ProviderType.Flatrate && userProviders.Contains(p.ProviderId))];

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

        // parse watch providers
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
    };

    private async Task<T?> GetAsync<T>(string url)
    {
        try
        {
            HttpResponseMessage response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
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
    }
}
