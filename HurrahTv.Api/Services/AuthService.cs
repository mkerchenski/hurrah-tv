using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HurrahTv.Api.Services;

public class AuthService(DbService db, SmsService sms, IConfiguration config)
{
    private readonly DbService _db = db;
    private readonly SmsService _sms = sms;
    private readonly string _jwtKey = config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required");
    private readonly string _jwtIssuer = config["Jwt:Issuer"] ?? "HurrahTv";

    public async Task<bool> SendCodeAsync(string phoneNumber)
    {
        string code = GenerateCode();
        await _db.SaveOtpCodeAsync(phoneNumber, code);
        await _sms.SendCodeAsync(phoneNumber, code);
        return true;
    }

    public async Task<string?> VerifyCodeAsync(string phoneNumber, string code)
    {
        bool valid = await _db.VerifyOtpCodeAsync(phoneNumber, code);
        if (!valid) return null;

        // ensure user exists
        string userId = await _db.GetOrCreateUserAsync(phoneNumber);
        return GenerateToken(userId, phoneNumber);
    }

    private string GenerateToken(string userId, string phoneNumber)
    {
        byte[] keyBytes = Convert.FromBase64String(_jwtKey);
        SymmetricSecurityKey key = new(keyBytes);

        JsonWebTokenHandler handler = new();
        string token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", userId),
                new Claim("phone", phoneNumber)
            ]),
            Expires = DateTime.UtcNow.AddDays(90),
            Issuer = _jwtIssuer,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature)
        });

        return token;
    }

    private static string GenerateCode() => RandomNumberGenerator.GetInt32(100000, 999999).ToString();
}
