using System.ComponentModel.DataAnnotations;

namespace dotnetai.Api.Services;

/// <summary>
/// Strongly-typed view of the "Jwt" section in appsettings.json.
///
/// THE OPTIONS PATTERN: rather than reaching for IConfiguration["Jwt:Issuer"] all over
/// the codebase (stringly-typed, no validation, typos fail silently at runtime), you bind
/// a config section onto a class once and inject IOptions&lt;JwtOptions&gt; where you need it.
/// You get compile-time names, real types, and — with the DataAnnotations below — validation.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Who minted the token. Validated on the way back in, so a token signed by
    /// some *other* system that happens to share our key is still rejected.</summary>
    [Required, MinLength(1)]
    public string Issuer { get; init; } = "";

    /// <summary>Who the token is *for*. Stops a token we issued for service A being
    /// replayed against service B.</summary>
    [Required, MinLength(1)]
    public string Audience { get; init; } = "";

    /// <summary>
    /// The HMAC signing secret. NOT in appsettings.json — that file is committed and shipped.
    /// In development it comes from user-secrets (stored in ~/.microsoft/usersecrets/, outside
    /// the repo). In production it comes from an environment variable (Jwt__Key) or a vault.
    ///
    /// The 32-character floor is not arbitrary: HMAC-SHA256 requires a key of at least
    /// 256 bits, and this string becomes bytes via Encoding.UTF8.GetBytes — so 32 ASCII
    /// characters is exactly 256 bits. A shorter key throws a cryptic IDX10653 from deep
    /// inside Microsoft.IdentityModel at token-creation time. Catching it here instead turns
    /// that into a startup failure that names the config key.
    /// </summary>
    [Required, MinLength(32, ErrorMessage =
        "Jwt:Key must be at least 32 characters (256 bits) for HMAC-SHA256. " +
        "Generate one with: dotnet user-secrets set \"Jwt:Key\" \"$(openssl rand -base64 48)\"")]
    public string Key { get; init; } = "";

    /// <summary>How long an access token is usable. Short on purpose: an access token
    /// cannot be revoked, so this is your blast radius if one leaks.</summary>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 15;

    /// <summary>How long a refresh token is usable. Long, because this one CAN be revoked
    /// (it's a database row, not a signature).</summary>
    [Range(1, 365)]
    public int RefreshTokenDays { get; init; } = 14;
}
