using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HurrahTv.Api.Tests;

// mints JWTs for tests using the same key/issuer the test API is configured with.
// keeps the test surface tight by bypassing the SMS/OTP flow — auth endpoints aren't
// what's being tested here, the protected endpoints downstream of the JWT are.
public static class TestAuth
{
    public static string IssueToken(PostgresFixture fx, string userId, TimeSpan? lifetime = null)
    {
        SymmetricSecurityKey key = new(Convert.FromBase64String(fx.JwtKey));
        JsonWebTokenHandler handler = new();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", userId), new Claim("phone", "+15555550100")]),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(30)),
            Issuer = PostgresFixture.JwtIssuer,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature)
        });
    }

    public static string IssueExpiredToken(PostgresFixture fx, string userId)
    {
        SymmetricSecurityKey key = new(Convert.FromBase64String(fx.JwtKey));
        JsonWebTokenHandler handler = new();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", userId)]),
            // backdated so the token is already expired when minted
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddHours(-1),
            Issuer = PostgresFixture.JwtIssuer,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature)
        });
    }

    public static HttpClient CreateClient(PostgresFixture fx, string userId)
    {
        HttpClient client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", IssueToken(fx, userId));
        return client;
    }
}
