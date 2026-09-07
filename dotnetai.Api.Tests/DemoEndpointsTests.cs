using System.Net;
using System.Text.Json;
using dotnetai.Api.Models;

namespace dotnetai.Api.Tests;

/// <summary>
/// The three access levels, and the 401-vs-403 distinction that beginners most often get wrong:
/// 401 means "I do not know who you are", 403 means "I know exactly who you are and the answer
/// is still no". Getting a 401 where you expected a 403 usually means the token never arrived.
/// </summary>
public sealed class DemoEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_root_is_anonymous()
    {
        var response = await factory.CreateApiClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello World!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Weatherforecast_requires_a_token()
    {
        var response = await factory.CreateApiClient().GetAsync("/weatherforecast");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Weatherforecast_returns_five_days_to_a_signed_in_user()
    {
        var (client, _, _) = await factory.CreateSignedInClientAsync();

        var response = await client.GetAsync("/weatherforecast");
        response.EnsureSuccessStatusCode();

        var days = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(5, days.GetArrayLength());

        foreach (var day in days.EnumerateArray())
        {
            Assert.True(day.TryGetProperty("date", out _));
            Assert.True(day.TryGetProperty("temperatureC", out _));
            Assert.True(day.TryGetProperty("temperatureF", out _));
            Assert.True(day.TryGetProperty("summary", out _));
        }
    }

    /// <summary>A plain User is authenticated but not authorized — 403, not 401.</summary>
    [Fact]
    public async Task Admin_users_is_forbidden_for_a_plain_user()
    {
        var (client, _, _) = await factory.CreateSignedInClientAsync(role: Roles.User);

        var response = await client.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Nobody at all is unauthenticated — 401, not 403.</summary>
    [Fact]
    public async Task Admin_users_is_unauthorized_for_an_anonymous_caller()
    {
        var response = await factory.CreateApiClient().GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_users_is_allowed_for_an_admin()
    {
        var email = ApiFactory.UniqueEmail();
        var (client, _, _) = await factory.CreateSignedInClientAsync(email, role: Roles.Admin);

        var response = await client.GetAsync("/admin/users");
        response.EnsureSuccessStatusCode();

        Assert.Contains(email.ToLowerInvariant(), await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The endpoint projects to UserSummary rather than returning entities. Return db.Users
    /// directly and every password hash in the database goes out over HTTP — admins do not get
    /// to see those either. This is the test that catches that refactor.
    /// </summary>
    [Fact]
    public async Task Admin_users_never_returns_password_hashes()
    {
        var email = ApiFactory.UniqueEmail();
        const string password = "correct-horse-battery";
        var seeded = await factory.SeedUserAsync(email, password);

        var (client, _, _) = await factory.CreateSignedInClientAsync(role: Roles.Admin);

        var response = await client.GetAsync("/admin/users");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(email, body);                    // the account is listed...
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.PasswordHash, body);   // ...but its hash is not
        Assert.DoesNotContain(password, body);
    }

    [Fact]
    public async Task Admin_users_returns_the_expected_summary_shape()
    {
        var (client, _, _) = await factory.CreateSignedInClientAsync(role: Roles.Admin);

        var response = await client.GetAsync("/admin/users");
        var users = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var first = users.EnumerateArray().First();
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("email", out _));
        Assert.True(first.TryGetProperty("role", out _));
        Assert.True(first.TryGetProperty("createdAtUtc", out _));
        Assert.Equal(4, first.EnumerateObject().Count());
    }

    /// <summary>An expired or forged token on a role-gated endpoint is a 401, not a 403 —
    /// authentication fails first, so there is no principal for the policy to reject.</summary>
    [Fact]
    public async Task A_forged_admin_token_gets_401_not_403()
    {
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", TestTokens.SignedWithAttackerKey(Guid.CreateVersion7().ToString(), Roles.Admin));

        var response = await client.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
