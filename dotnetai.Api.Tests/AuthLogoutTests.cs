using System.Net;
using System.Net.Http.Json;
using dotnetai.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Tests;

public sealed class AuthLogoutTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        var response = await PostLogoutAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var hash = TokenService.Hash(tokens.RefreshToken);
        var row = await factory.WithDbAsync(db => db.RefreshTokens.SingleAsync(rt => rt.TokenHash == hash));

        Assert.NotNull(row.RevokedAtUtc);
        Assert.Equal("logout", row.RevokedReason);
    }

    [Fact]
    public async Task After_logout_the_token_cannot_be_refreshed()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        await PostLogoutAsync(tokens.RefreshToken);

        var refresh = await factory.CreateApiClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    /// <summary>
    /// Logout must be idempotent AND must not become an oracle for "is this token real?".
    /// A 404 for an unknown token would let an attacker test stolen tokens without spending them.
    /// </summary>
    [Fact]
    public async Task Logging_out_an_unknown_token_still_returns_204()
    {
        var response = await PostLogoutAsync("this-token-was-never-issued");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Logging_out_twice_is_harmless_and_keeps_the_original_reason()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostLogoutAsync(tokens.RefreshToken)).StatusCode);

        var hash = TokenService.Hash(tokens.RefreshToken);
        var firstRevokedAt = (await factory.WithDbAsync(db =>
            db.RefreshTokens.SingleAsync(rt => rt.TokenHash == hash))).RevokedAtUtc;

        Assert.Equal(HttpStatusCode.NoContent, (await PostLogoutAsync(tokens.RefreshToken)).StatusCode);

        var after = await factory.WithDbAsync(db => db.RefreshTokens.SingleAsync(rt => rt.TokenHash == hash));
        Assert.Equal(firstRevokedAt, after.RevokedAtUtc);
        Assert.Equal("logout", after.RevokedReason);
    }

    /// <summary>Signing out of the phone must not sign out the laptop.</summary>
    [Fact]
    public async Task Logout_only_ends_the_session_whose_token_was_presented()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, "correct-horse-battery");

        var phone = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");
        var laptop = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");

        await PostLogoutAsync(phone.RefreshToken);

        var laptopRefresh = await factory.CreateApiClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = laptop.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, laptopRefresh.StatusCode);
    }

    /// <summary>
    /// THE HONEST LIMIT OF STATELESS JWTs. Logout revokes the refresh token, but the access token
    /// already in the client's hands stays valid until it expires — the server does not consult a
    /// database to validate one, which is exactly why it is fast and exactly why it cannot be
    /// un-issued. This test documents that rather than pretending otherwise; if you ever add a
    /// denylist, this is the test that will fail and tell you the behaviour changed.
    /// </summary>
    [Fact]
    public async Task The_already_issued_access_token_survives_logout()
    {
        var (client, tokens, _) = await factory.CreateSignedInClientAsync();

        await PostLogoutAsync(tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_does_not_require_an_access_token()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        var anonymous = factory.CreateApiClient();
        Assert.Null(anonymous.DefaultRequestHeaders.Authorization);

        var response = await anonymous.PostAsJsonAsync("/auth/logout",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostLogoutAsync(string refreshToken) =>
        factory.CreateApiClient().PostAsJsonAsync("/auth/logout", new { refreshToken });
}
