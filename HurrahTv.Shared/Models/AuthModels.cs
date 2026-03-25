namespace HurrahTv.Shared.Models;

public record SendCodeRequest(string PhoneNumber);

public record VerifyCodeRequest(string PhoneNumber, string Code);

public record AuthResponse(string Token, string PhoneNumber);
