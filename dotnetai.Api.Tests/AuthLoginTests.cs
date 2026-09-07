using System.Net;
using System.Net.Http.Json;
using dotnetai.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Tests;

public sealed class AuthLoginTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task Valid_credentials_return_an_access_and_refresh_token()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var tokens = await factory.LoginAsync(factory.CreateApiClient(), email, Password);

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.True(tokens.AccessTokenExpiresAtUtc > DateTime.UtcNow);
        Assert.True(tokens.RefreshTokenExpiresAtUtc > tokens.AccessTokenExpiresAtUtc);
    }

    [Fact]
    public async Task Access_token_is_a_three_segment_jwt()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        Assert.Equal(3, tokens.AccessToken.Split('.').Length);
    }

    /// <summary>
    /// The refresh token is a bearer credential, same class as a password. Only its SHA-256 goes
    /// in the database, so a leaked backup yields useless hashes rather than live sessions.
    /// </summary>
    [Fact]
    public async Task Refresh_token_is_stored_hashed_never_raw()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var tokens = await factory.LoginAsync(factory.CreateApiClient(), email, Password);

        var rawIsStored = await factory.WithDbAsync(db =>
            db.RefreshTokens.AnyAsync(rt => rt.TokenHash == tokens.RefreshToken));
        Assert.False(rawIsStored);

        var hashIsStored = await factory.WithDbAsync(db =>
            db.RefreshTokens.AnyAsync(rt => rt.TokenHash == TokenService.Hash(tokens.RefreshToken)));
        Assert.True(hashIsStored);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("MiXeD")]
    public async Task Email_matching_ignores_case(string style)
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var typed = style == "UPPER" ? email.ToUpperInvariant() : ToMixedCase(email);
        var tokens = await factory.LoginAsync(factory.CreateApiClient(), typed, Password);

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
    }

    [Fact]
    public async Task Surrounding_whitespace_in_the_email_is_ignored()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var tokens = await factory.LoginAsync(factory.CreateApiClient(), $"  {email}  ", Password);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var response = await factory.CreateApiClient()
            .PostAsJsonAsync("/auth/login", new { email, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Password_matching_is_case_sensitive()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var response = await factory.CreateApiClient()
            .PostAsJsonAsync("/auth/login", new { email, password = Password.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// THE ENUMERATION ORACLE, again. If "no such user" and "wrong password" differed in status
    /// code or body, an attacker could sort a leaked address list into "has an account here" and
    /// "does not" without ever guessing a password. Assert they are byte-for-byte identical.
    /// </summary>
    [Fact]
    public async Task Unknown_email_and_wrong_password_are_indistinguishable()
    {
        var known = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(known, Password);
        var client = factory.CreateApiClient();

        var wrongPassword = await client.PostAsJsonAsync("/auth/login",
            new { email = known, password = "not-the-password" });
        var unknownUser = await client.PostAsJsonAsync("/auth/login",
            new { email = ApiFactory.UniqueEmail(), password = Password });

        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Failed_login_issues_no_refresh_token()
    {
        var email = ApiFactory.UniqueEmail();
        var user = await factory.SeedUserAsync(email, Password);

        await factory.CreateApiClient()
            .PostAsJsonAsync("/auth/login", new { email, password = "not-the-password" });

        var count = await factory.WithDbAsync(db =>
            db.RefreshTokens.CountAsync(rt => rt.UserId == user.Id));
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Signing in twice is two independent sessions — a phone and a laptop. Logging in on one
    /// must not revoke the other, and each login gets its own refresh-token row.
    /// </summary>
    [Fact]
    public async Task Two_logins_produce_two_independent_sessions()
    {
        var email = ApiFactory.UniqueEmail();
        var user = await factory.SeedUserAsync(email, Password);

        var first = await factory.LoginAsync(factory.CreateApiClient(), email, Password);
        var second = await factory.LoginAsync(factory.CreateApiClient(), email, Password);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var live = await factory.WithDbAsync(db =>
            db.RefreshTokens.CountAsync(rt => rt.UserId == user.Id && rt.RevokedAtUtc == null));
        Assert.Equal(2, live);
    }

    [Fact]
    public async Task Login_response_does_not_leak_the_password_hash()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, Password);

        var response = await factory.CreateApiClient()
            .PostAsJsonAsync("/auth/login", new { email, password = Password });
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, body);
    }

    private static string ToMixedCase(string value) =>
        string.Concat(value.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c));
}
