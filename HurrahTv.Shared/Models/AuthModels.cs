namespace HurrahTv.Shared.Models;

public record SendCodeRequest(string PhoneNumber);

public record VerifyCodeRequest(string PhoneNumber, string Code);

public record AuthResponse(string Token, string PhoneNumber);

public record UserProfile(string PhoneNumber, string? FirstName);

public record UpdateProfileRequest(string? FirstName);
