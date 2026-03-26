using System.Net.Http.Json;
using HurrahTv.Shared.Models;

namespace HurrahTv.Client.Services;

public class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    // auth
    public async Task<bool> SendCodeAsync(string phoneNumber)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/auth/send-code", new SendCodeRequest(phoneNumber));
        return response.IsSuccessStatusCode;
    }

    public async Task<AuthResponse?> VerifyCodeAsync(string phoneNumber, string code)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/auth/verify", new VerifyCodeRequest(phoneNumber, code));
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<AuthResponse>();
        return null;
    }

    // search
    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1) =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search?q={Uri.EscapeDataString(query)}&page={page}") ?? [];

    public async Task<List<SearchResult>> TrendingAsync(string mediaType = "all") =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/trending?mediaType={mediaType}") ?? [];

    public async Task<List<SearchResult>> ForYouAsync() =>
        await _http.GetFromJsonAsync<List<SearchResult>>("api/search/for-you") ?? [];

    public async Task<List<SearchResult>> DiscoverByProviderAsync(int providerId, string mediaType = "tv") =>
        await _http.GetFromJsonAsync<List<SearchResult>>($"api/search/provider/{providerId}?mediaType={mediaType}") ?? [];

    // details
    public async Task<ShowDetails?> GetDetailsAsync(int tmdbId, string mediaType) =>
        await _http.GetFromJsonAsync<ShowDetails>($"api/details/{mediaType}/{tmdbId}");

    // queue
    public async Task<List<QueueItem>> GetQueueAsync() =>
        await _http.GetFromJsonAsync<List<QueueItem>>("api/queue") ?? [];

    public async Task<QueueItem?> AddToQueueAsync(QueueItem item)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/queue", item);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<QueueItem>();
        return null;
    }

    public async Task RemoveFromQueueAsync(int id) =>
        await _http.DeleteAsync($"api/queue/{id}");

    public async Task UpdateStatusAsync(int id, QueueStatus status) =>
        await _http.PutAsJsonAsync($"api/queue/{id}/status", new { Status = status });

    // user services
    public async Task<List<int>> GetUserServicesAsync() =>
        await _http.GetFromJsonAsync<List<int>>("api/services") ?? [];

    public async Task SetUserServicesAsync(List<int> providerIds) =>
        await _http.PutAsJsonAsync("api/services", providerIds);
}
