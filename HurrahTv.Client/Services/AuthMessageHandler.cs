using System.Net.Http.Headers;

namespace HurrahTv.Client.Services;

// adds the JWT token to every outgoing HTTP request
public class AuthMessageHandler(TokenService tokenService) : DelegatingHandler
{
    private readonly TokenService _tokenService = tokenService;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? token = await _tokenService.GetTokenAsync();
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
