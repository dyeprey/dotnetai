using System.ComponentModel.DataAnnotations;

namespace dotnetai.Api.Services;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required, MinLength(1)]
    public string Issuer { get; init; } = "";

    [Required, MinLength(1)]
    public string Audience { get; init; } = "";

    [Required, MinLength(32, ErrorMessage =
        "Jwt:Key must be at least 32 characters (256 bits) for HMAC-SHA256. " +
        "Generate one with: dotnet user-secrets set \"Jwt:Key\" \"$(openssl rand -base64 48)\"")]
    public string Key { get; init; } = "";

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; init; } = 14;
}
