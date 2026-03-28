using Microsoft.JSInterop;

namespace HurrahTv.Client.Services;

// stores JWT token in localStorage for persistence across sessions
public class TokenService(IJSRuntime js)
{
    private readonly IJSRuntime _js = js;
    private string? _cachedToken;

    public async Task<string?> GetTokenAsync()
    {
        _cachedToken ??= await _js.InvokeAsync<string?>("localStorage.getItem", "auth_token");
        return _cachedToken;
    }

    public async Task SetTokenAsync(string token)
    {
        _cachedToken = token;
        await _js.InvokeVoidAsync("localStorage.setItem", "auth_token", token);
    }

    public async Task ClearTokenAsync()
    {
        _cachedToken = null;
        await _js.InvokeVoidAsync("localStorage.removeItem", "auth_token");
    }
}
