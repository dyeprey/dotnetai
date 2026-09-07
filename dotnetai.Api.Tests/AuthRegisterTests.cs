using System.Net;
using System.Net.Http.Json;
using System.Text;
using dotnetai.Api.Dtos;
using dotnetai.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Tests;

public sealed class AuthRegisterTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string GenericMessage = "If that email is available, the account has been created.";

    [Fact]
    public async Task Valid_registration_creates_the_user()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/auth/register",
            new { email, password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(ApiFactory.Json);
        Assert.Equal(GenericMessage, body!.Message);

        var stored = await factory.WithDbAsync(db => db.Users.SingleOrDefaultAsync(u => u.Email == email));
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task Password_is_hashed_and_the_plaintext_is_never_stored()
    {
        const string password = "correct-horse-battery";
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        await client.PostAsJsonAsync("/auth/register", new { email, password });

        var stored = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == email));

        Assert.NotEqual(password, stored.PasswordHash);
        Assert.DoesNotContain(password, stored.PasswordHash);
        // Identity's PBKDF2 format is a base64 blob with the algorithm and iteration count
        // encoded in it — comfortably longer than any plausible plaintext.
        Assert.True(stored.PasswordHash.Length > 40);
    }

    [Fact]
    public async Task New_accounts_are_always_role_User()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        await client.PostAsJsonAsync("/auth/register", new { email, password = "correct-horse-battery" });

        var stored = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == email));
        Assert.Equal(Roles.User, stored.Role);
    }

    /// <summary>
    /// MASS ASSIGNMENT. RegisterRequest has no Role property, so a client that posts one has
    /// nowhere for the binder to put it. This is the test that proves the DTO split in Dtos/ is
    /// load-bearing rather than decorative — delete RegisterRequest and bind to User directly
    /// and this test is the one that catches it.
    /// </summary>
    [Fact]
    public async Task Role_in_the_request_body_cannot_promote_the_account()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        // Hand-written JSON rather than an anonymous object, for two reasons: it is exactly what
        // an attacker would put on the wire, and it lets us send "role" AND "Role" together —
        // a pair that no C# anonymous type can express.
        var json = $$"""
            {
              "email": "{{email}}",
              "password": "correct-horse-battery",
              "role": "Admin",
              "Role": "Admin"
            }
            """;

        var response = await client.PostAsync("/auth/register",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == email));
        Assert.Equal(Roles.User, stored.Role);
    }

    [Theory]
    [InlineData("short1")]         // 6 characters
    [InlineData("1234567")]        // 7 — one short of the boundary
    [InlineData("")]
    public async Task Passwords_under_eight_characters_are_rejected(string password)
    {
        var client = factory.CreateApiClient();
        var email = ApiFactory.UniqueEmail();

        var response = await client.PostAsJsonAsync("/auth/register", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Exactly_eight_characters_is_accepted()
    {
        // The boundary itself: `req.Password is not { Length: >= 8 }`. Off-by-one guard.
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/auth/register", new { email, password = "12345678" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_email_is_rejected(string email)
    {
        var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/auth/register",
            new { email, password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// ACCOUNT ENUMERATION. The whole point of returning 200 rather than 409 is that a caller
    /// cannot tell "created" from "already taken". So assert on the thing an attacker would
    /// actually measure: the status code and the response body, byte for byte.
    /// </summary>
    [Fact]
    public async Task Duplicate_registration_is_indistinguishable_from_a_new_one()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();
        var payload = new { email, password = "correct-horse-battery" };

        var first = await client.PostAsJsonAsync("/auth/register", payload);
        var second = await client.PostAsJsonAsync("/auth/register", payload);

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Duplicate_registration_does_not_create_a_second_user()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();
        var payload = new { email, password = "correct-horse-battery" };

        await client.PostAsJsonAsync("/auth/register", payload);
        await client.PostAsJsonAsync("/auth/register", payload);

        var count = await factory.WithDbAsync(db => db.Users.CountAsync(u => u.Email == email));
        Assert.Equal(1, count);
    }

    /// <summary>
    /// The second registration must not overwrite the first account's password — otherwise
    /// "register with an existing address" becomes a password reset with no proof of ownership,
    /// which is account takeover.
    /// </summary>
    [Fact]
    public async Task Duplicate_registration_does_not_change_the_existing_password()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        await client.PostAsJsonAsync("/auth/register", new { email, password = "original-password" });
        var before = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == email));

        await client.PostAsJsonAsync("/auth/register", new { email, password = "attacker-chosen-pw" });
        var after = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == email));

        Assert.Equal(before.PasswordHash, after.PasswordHash);

        // And prove it end to end: the original password still signs in.
        var tokens = await factory.LoginAsync(factory.CreateApiClient(), email, "original-password");
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
    }

    [Fact]
    public async Task Email_is_trimmed_and_lowercased_before_storage()
    {
        var raw = ApiFactory.UniqueEmail("MiXeD").ToUpperInvariant();
        var normalized = raw.Trim().ToLowerInvariant();
        var client = factory.CreateApiClient();

        await client.PostAsJsonAsync("/auth/register",
            new { email = $"   {raw}   ", password = "correct-horse-battery" });

        Assert.True(await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == normalized)));
    }

    /// <summary>
    /// SQLite's default TEXT collation is case-SENSITIVE, so the unique index alone would happily
    /// store both spellings. Normalization in application code is what prevents the duplicate —
    /// this test fails the moment someone removes the ToLowerInvariant call.
    /// </summary>
    [Fact]
    public async Task Registering_the_same_address_in_different_case_is_a_duplicate()
    {
        var email = ApiFactory.UniqueEmail();
        var client = factory.CreateApiClient();

        await client.PostAsJsonAsync("/auth/register", new { email, password = "correct-horse-battery" });
        await client.PostAsJsonAsync("/auth/register",
            new { email = email.ToUpperInvariant(), password = "correct-horse-battery" });

        var count = await factory.WithDbAsync(db =>
            db.Users.CountAsync(u => u.Email == email.ToLowerInvariant()));

        Assert.Equal(1, count);
    }

    /// <summary>
    /// Registration mints no tokens. Creating an account and being signed in are separate steps,
    /// and a response that leaked a token here would sign in whoever guessed an existing address.
    /// </summary>
    [Fact]
    public async Task Registration_does_not_return_any_token()
    {
        var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/auth/register",
            new { email = ApiFactory.UniqueEmail(), password = "correct-horse-battery" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
    }
}
