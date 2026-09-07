using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using dotnetai.Api.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace dotnetai.Api.Services;

/// <summary>
/// The single place that knows how to mint a token. Isolating it means (a) it is pure logic
/// with no HTTP in it, so it is unit-testable, and (b) the claim set is defined exactly once,
/// so the claims we sign here and the claims we validate in Program.cs cannot drift apart.
/// </summary>
public sealed class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _jwt = options.Value;

    // JsonWebTokenHandler is designed to be reused; ASP.NET Core itself keeps a single instance
    // inside JwtBearerOptions.TokenHandlers.
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>
    /// Builds a signed JWT. A JWT is three base64url segments joined by dots:
    ///   header.payload.signature
    /// The header and payload are only ENCODED, not encrypted — anyone holding the token can
    /// read every claim in it. The signature is what makes it trustworthy: it proves the payload
    /// has not been altered since we signed it. So never put a secret in a claim.
    /// </summary>
    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);

        // HMAC-SHA256 requires a key of at least 256 bits. JwtOptions' [MinLength(32)] plus
        // ValidateOnStart already guaranteed that at startup, so this cannot fail here.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = now,     // "iat"
            NotBefore = now,    // "nbf" — reject the token if presented before this instant
            Expires = expires,  // "exp"
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),

            // USE Claims (the dictionary), NOT Subject (a ClaimsIdentity). This is not style.
            //
            // JsonWebTokenHandler has NO outbound claim-type map. Hand it a ClaimsIdentity
            // holding a ClaimTypes.Role claim and the JSON key in the token becomes the literal
            // string "http://schemas.microsoft.com/ws/2008/06/identity/claims/role" — a 60-byte
            // URI in every request header, for a value of "User". The older
            // JwtSecurityTokenHandler would have shortened it; this one does not.
            //
            // The dictionary gives us exact control, so we write the short, standard names.
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),    // "subject" — who this is
                [JwtRegisteredClaimNames.Email] = user.Email,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"), // unique token id
                ["role"] = user.Role,
            },
        };

        return (Handler.CreateToken(descriptor), expires);
    }

    /// <summary>
    /// Returns (raw token for the client, SHA-256 hex to store in the database).
    ///
    /// This is deliberately NOT a JWT. It carries no claims and is not self-validating — it is
    /// an opaque lookup key whose entire meaning is "there is a row in RefreshTokens with this
    /// hash". That is the point: a JWT cannot be revoked, but a database row can be, instantly.
    ///
    /// 32 bytes from a cryptographically secure RNG is 256 bits of entropy. Guessing one is
    /// not merely impractical, it is physically impossible.
    /// </summary>
    public static (string Raw, string Hash) CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Base64Url.EncodeToString(bytes);   // URL-safe, unpadded
        return (raw, Hash(raw));
    }

    /// <summary>
    /// Plain SHA-256 — and yes, that is correct here even though passwords need PBKDF2.
    ///
    /// Passwords are low-entropy and human-chosen, so an attacker holding the hashes can guess
    /// them; you need a DELIBERATELY SLOW function to make each guess expensive. A refresh token
    /// is 256 bits of uniform randomness — there is nothing to guess, at any speed. So a fast
    /// hash is not merely acceptable, it is right, and it keeps /auth/refresh from burning 100ms
    /// of key stretching on every call.
    ///
    /// NEVER apply this reasoning to passwords.
    /// </summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
