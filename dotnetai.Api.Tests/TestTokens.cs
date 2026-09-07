using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace dotnetai.Api.Tests;

/// <summary>
/// Mints JWTs with deliberately WRONG parts, so each clause of TokenValidationParameters in
/// Program.cs can be tested on its own.
///
/// The API's own TokenService cannot do this — it only ever produces correct tokens, which is
/// exactly what you want from production code and exactly what makes it useless for proving
/// that a bad token is rejected. Every setting in that validation block is a security boundary,
/// and a boundary nobody tests is a boundary you are assuming.
/// </summary>
public static class TestTokens
{
    private static readonly JsonWebTokenHandler Handler = new();

    public static string Create(
        string subject,
        string email = "someone@example.com",
        string role = "User",
        string issuer = ApiFactory.Issuer,
        string audience = ApiFactory.Audience,
        string key = ApiFactory.JwtKey,
        TimeSpan? lifetime = null,
        TimeSpan? notBeforeOffset = null)
    {
        var now = DateTime.UtcNow;
        var notBefore = now.Add(notBeforeOffset ?? TimeSpan.Zero);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = notBefore,
            Expires = notBefore.Add(lifetime ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = subject,
                [JwtRegisteredClaimNames.Email] = email,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                ["role"] = role,
            },
        };

        return Handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Flips the last character of the SIGNATURE segment, leaving the payload intact. This is
    /// the closest thing to a real attack: the token still parses, still carries whatever claims
    /// it claims to carry, and is rejected purely because the signature no longer verifies.
    /// </summary>
    public static string WithBrokenSignature(string token)
    {
        var parts = token.Split('.');
        var signature = parts[2];
        var last = signature[^1];

        parts[2] = signature[..^1] + (last == 'A' ? 'B' : 'A');
        return string.Join('.', parts);
    }

    /// <summary>
    /// Re-signs a token's ORIGINAL claims with an attacker's key. Proves ValidateIssuerSigningKey
    /// is doing its job — without it, anyone could hand us a token declaring role=Admin.
    /// </summary>
    public static string SignedWithAttackerKey(string subject, string role = "Admin") =>
        Create(subject, role: role, key: "attacker-key-also-long-enough-for-hmac-256bits");
}
