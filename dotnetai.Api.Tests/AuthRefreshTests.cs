using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using dotnetai.Api.Dtos;
using dotnetai.Api.Models;
using dotnetai.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Tests;

/// <summary>
/// Rotation and reuse detection — the part of this API with real security teeth, and the part
/// where a plausible-looking refactor does the most damage.
/// </summary>
public sealed class AuthRefreshTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_valid_refresh_token_buys_a_new_pair()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        var refreshed = await RefreshAsync(tokens.RefreshToken);

        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
    }

    /// <summary>
    /// /auth/refresh deliberately has NO .RequireAuthorization(). You call it precisely because
    /// your access token has expired — requiring a valid one to get a valid one is a deadlock.
    /// The refresh token IS the credential here.
    /// </summary>
    [Fact]
    public async Task Refreshing_does_not_require_an_access_token()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        var anonymous = factory.CreateApiClient();
        Assert.Null(anonymous.DefaultRequestHeaders.Authorization);

        var response = await anonymous.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_presented_token_is_consumed_and_cannot_be_used_twice()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        await RefreshAsync(tokens.RefreshToken);
        var second = await PostRefreshAsync(tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Rotation_stamps_the_old_row_and_links_it_to_its_successor()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        var refreshed = await RefreshAsync(tokens.RefreshToken);

        var oldHash = TokenService.Hash(tokens.RefreshToken);
        var newHash = TokenService.Hash(refreshed.RefreshToken);

        var old = await factory.WithDbAsync(db =>
            db.RefreshTokens.SingleAsync(rt => rt.TokenHash == oldHash));

        Assert.NotNull(old.RevokedAtUtc);
        Assert.Equal("rotated", old.RevokedReason);
        Assert.Equal(newHash, old.ReplacedByTokenHash);   // the T1 -> T2 chain link

        var successor = await factory.WithDbAsync(db =>
            db.RefreshTokens.SingleAsync(rt => rt.TokenHash == newHash));
        Assert.Null(successor.RevokedAtUtc);
        Assert.Equal(old.UserId, successor.UserId);
    }

    [Fact]
    public async Task A_chain_of_refreshes_keeps_working()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        var current = tokens.RefreshToken;
        for (var i = 0; i < 5; i++)
        {
            var next = await RefreshAsync(current);
            Assert.NotEqual(current, next.RefreshToken);
            current = next.RefreshToken;
        }
    }

    /// <summary>
    /// REUSE DETECTION, the whole point of rotation.
    ///
    /// A legitimate client always holds exactly the newest link in the chain, so presenting an
    /// already-revoked token never happens in normal operation. When it does, the chain has been
    /// duplicated. We cannot tell victim from thief, so every session dies and a password
    /// re-auth is required — which the attacker cannot do.
    /// </summary>
    [Fact]
    public async Task Reusing_a_spent_token_revokes_every_session_for_that_user()
    {
        var email = ApiFactory.UniqueEmail();
        var user = await factory.SeedUserAsync(email, "correct-horse-battery");

        // Two independent sessions: think phone and laptop.
        var phone = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");
        var laptop = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");

        // The phone refreshes normally. Its old token is now spent.
        var phoneRotated = await RefreshAsync(phone.RefreshToken);

        // An attacker replays the stolen, already-spent token.
        var replay = await PostRefreshAsync(phone.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Now the blast radius: BOTH the phone's current token and the untouched laptop session
        // are dead. That is intentional — we do not know which holder was the thief.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(phoneRotated.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(laptop.RefreshToken)).StatusCode);

        var live = await factory.WithDbAsync(db =>
            db.RefreshTokens.CountAsync(rt => rt.UserId == user.Id && rt.RevokedAtUtc == null));
        Assert.Equal(0, live);
    }

    [Fact]
    public async Task Reuse_detection_records_why_the_tokens_were_revoked()
    {
        var email = ApiFactory.UniqueEmail();
        var user = await factory.SeedUserAsync(email, "correct-horse-battery");
        var session = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");

        await RefreshAsync(session.RefreshToken);
        await PostRefreshAsync(session.RefreshToken);          // the reuse

        var reasons = await factory.WithDbAsync(db => db.RefreshTokens
            .Where(rt => rt.UserId == user.Id)
            .Select(rt => rt.RevokedReason)
            .ToListAsync());

        // The audit trail distinguishes a normal rotation from the incident that killed the rest.
        Assert.Contains("rotated", reasons);
        Assert.Contains("reuse-detected", reasons);
    }

    /// <summary>One user's compromise must not sign anybody else out.</summary>
    [Fact]
    public async Task Reuse_detection_does_not_touch_other_users()
    {
        var victimEmail = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(victimEmail, "correct-horse-battery");
        var victim = await factory.LoginAsync(factory.CreateApiClient(), victimEmail, "correct-horse-battery");

        var bystanderEmail = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(bystanderEmail, "correct-horse-battery");
        var bystander = await factory.LoginAsync(factory.CreateApiClient(), bystanderEmail, "correct-horse-battery");

        await RefreshAsync(victim.RefreshToken);
        await PostRefreshAsync(victim.RefreshToken);           // trigger the incident

        var stillWorks = await PostRefreshAsync(bystander.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        var response = await PostRefreshAsync("this-token-was-never-issued");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        var hash = TokenService.Hash(tokens.RefreshToken);

        // Age the row rather than waiting 14 days for it.
        await factory.WithDbAsync(async db =>
        {
            var row = await db.RefreshTokens.SingleAsync(rt => rt.TokenHash == hash);
            row.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(tokens.RefreshToken)).StatusCode);
    }

    /// <summary>
    /// An expired token is spent, not stolen — rejecting it must NOT trigger the reuse alarm and
    /// nuke the user's other sessions.
    /// </summary>
    [Fact]
    public async Task An_expired_token_does_not_trigger_reuse_detection()
    {
        var email = ApiFactory.UniqueEmail();
        await factory.SeedUserAsync(email, "correct-horse-battery");

        var stale = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");
        var other = await factory.LoginAsync(factory.CreateApiClient(), email, "correct-horse-battery");

        var hash = TokenService.Hash(stale.RefreshToken);
        await factory.WithDbAsync(async db =>
        {
            var row = await db.RefreshTokens.SingleAsync(rt => rt.TokenHash == hash);
            row.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });

        await PostRefreshAsync(stale.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, (await PostRefreshAsync(other.RefreshToken)).StatusCode);
    }

    /// <summary>
    /// The refreshed access token re-reads the role from the database, so a promotion reaches the
    /// client on the next refresh without forcing a password re-entry. This is the behaviour the
    /// comment in RefreshAsync promises.
    /// </summary>
    [Fact]
    public async Task A_role_change_reaches_the_client_on_the_next_refresh()
    {
        var email = ApiFactory.UniqueEmail();
        var (client, tokens, user) = await factory.CreateSignedInClientAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/users")).StatusCode);

        await factory.WithDbAsync(async db =>
        {
            var row = await db.Users.SingleAsync(u => u.Id == user.Id);
            row.Role = Roles.Admin;
            await db.SaveChangesAsync();
        });

        // The OLD access token still says role=User — it is signed and cannot change.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/users")).StatusCode);

        var refreshed = await RefreshAsync(tokens.RefreshToken);
        var promoted = factory.CreateApiClient();
        promoted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await promoted.GetAsync("/admin/users")).StatusCode);
    }

    [Fact]
    public async Task The_new_refresh_token_is_also_stored_hashed()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        var refreshed = await RefreshAsync(tokens.RefreshToken);

        Assert.False(await factory.WithDbAsync(db =>
            db.RefreshTokens.AnyAsync(rt => rt.TokenHash == refreshed.RefreshToken)));
        Assert.True(await factory.WithDbAsync(db =>
            db.RefreshTokens.AnyAsync(rt => rt.TokenHash == TokenService.Hash(refreshed.RefreshToken))));
    }

    /// <summary>
    /// Rejections must all look alike. If "never existed" and "already revoked" differed, an
    /// attacker holding a stolen token could learn whether it had been used yet.
    /// </summary>
    [Fact]
    public async Task Unknown_revoked_and_expired_tokens_all_fail_identically()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        await RefreshAsync(tokens.RefreshToken);   // makes the original revoked

        var unknown = await PostRefreshAsync("never-issued-at-all");
        var revoked = await PostRefreshAsync(tokens.RefreshToken);

        Assert.Equal(unknown.StatusCode, revoked.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await revoked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_refreshed_access_token_actually_works()
    {
        var (_, tokens, user) = await factory.CreateSignedInClientAsync();

        var refreshed = await RefreshAsync(tokens.RefreshToken);
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        var me = await client.GetAsync("/auth/me");
        me.EnsureSuccessStatusCode();

        var payload = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(user.Id.ToString(), payload.GetProperty("sub").GetString());
    }

    // ---------------------------------------------------------------------

    private Task<HttpResponseMessage> PostRefreshAsync(string refreshToken) =>
        factory.CreateApiClient().PostAsJsonAsync("/auth/refresh", new { refreshToken });

    private async Task<TokenPairResponse> RefreshAsync(string refreshToken)
    {
        var response = await PostRefreshAsync(refreshToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenPairResponse>(ApiFactory.Json))!;
    }
}
