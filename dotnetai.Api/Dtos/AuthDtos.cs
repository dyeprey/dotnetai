namespace dotnetai.Api.Dtos;

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record MessageResponse(string Message);

public sealed record TokenPairResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record UserSummary(Guid Id, string Email, string Role, DateTime CreatedAtUtc);
