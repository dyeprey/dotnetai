using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using dotnetai.Api.Models;

namespace dotnetai.Api.Tests;

/// <summary>
/// /auth/me is a window onto exactly what the framework read out of a token, which makes it the
/// natural place to test the claim plumbing AND every clause of TokenValidationParameters.
/// </summary>
public sealed class AuthMeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Without_a_token_it_is_401()
    {
        var response = await factory.CreateApiClient().GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task With_a_valid_token_it_reports_the_signed_in_user()
    {
        var email = ApiFactory.UniqueEmail();
        var (client, _, user) = await factory.CreateSignedInClientAsync(email);

        var me = await ReadMeAsync(client);

        Assert.Equal(user.Id.ToString(), me.GetProperty("sub").GetString());
        Assert.Equal(email.ToLowerInvariant(), me.GetProperty("email").GetString());
        Assert.Equal(Roles.User, me.GetProperty("role").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
    }

    /// <summary>
    /// THE CLAIM-MAPPING TRAP. MapInboundClaims defaults to TRUE, and when it is on the handler
    /// rewrites "sub" and "role" into long WS-Federation URIs — so User.FindFirstValue("sub")
    /// returns null even though "sub" is plainly in the token. Program.cs turns it off; this is
    /// the test that keeps it off.
    /// </summary>
    [Fact]
    public async Task Claim_types_keep_their_short_names()
    {
        var (client, _, _) = await factory.CreateSignedInClientAsync();

        var me = await ReadMeAsync(client);
        var types = me.GetProperty("allClaims").EnumerateArray()
            .Select(c => c.GetProperty("type").GetString())
            .ToArray();

        Assert.Contains("sub", types);
        Assert.Contains("role", types);
        Assert.Contains("email", types);
        Assert.DoesNotContain(types, t => t!.StartsWith("http://schemas.", StringComparison.Ordinal));
    }

    /// <summary>
    /// user.Identity.Name is populated only because NameClaimType is set to "sub". Without that
    /// setting it is silently null, and nothing else in the app would tell you.
    /// </summary>
    [Fact]
    public async Task Name_resolves_because_NameClaimType_points_at_sub()
    {
        var (client, _, user) = await factory.CreateSignedInClientAsync();

        var me = await ReadMeAsync(client);
        Assert.Equal(user.Id.ToString(), me.GetProperty("name").GetString());
    }

    /// <summary>
    /// IsInRole works only because RoleClaimType is set to "role". Leave it at the default and
    /// every admin check fails with a 403 that gives no hint why.
    /// </summary>
    [Fact]
    public async Task IsAdmin_is_true_for_an_admin_because_RoleClaimType_is_set()
    {
        var (client, _, _) = await factory.CreateSignedInClientAsync(role: Roles.Admin);

        var me = await ReadMeAsync(client);
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(Roles.Admin, me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_garbage_string_is_not_a_token()
    {
        var response = await GetMeWithRawTokenAsync("not-a-jwt-at-all");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>ValidateIssuerSigningKey: the payload is untouched, only the signature is broken.</summary>
    [Fact]
    public async Task A_tampered_signature_is_rejected()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();

        var response = await GetMeWithRawTokenAsync(TestTokens.WithBrokenSignature(tokens.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The attack the signing key exists to stop: a well-formed token claiming role=Admin, signed
    /// with a key we never issued.
    /// </summary>
    [Fact]
    public async Task A_token_signed_with_someone_elses_key_is_rejected()
    {
        var response = await GetMeWithRawTokenAsync(
            TestTokens.SignedWithAttackerKey(Guid.CreateVersion7().ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>ValidateLifetime, well past the 5-second ClockSkew that Program.cs configures.</summary>
    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var token = TestTokens.Create(
            Guid.CreateVersion7().ToString(),
            lifetime: TimeSpan.FromMinutes(10),
            notBeforeOffset: TimeSpan.FromHours(-2));   // issued 2h ago, expired 1h50m ago

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeWithRawTokenAsync(token)).StatusCode);
    }

    /// <summary>"nbf" — a token minted for the future is not valid yet.</summary>
    [Fact]
    public async Task A_not_yet_valid_token_is_rejected()
    {
        var token = TestTokens.Create(
            Guid.CreateVersion7().ToString(),
            notBeforeOffset: TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeWithRawTokenAsync(token)).StatusCode);
    }

    /// <summary>ValidateIssuer: correctly signed, but minted by a different system.</summary>
    [Fact]
    public async Task A_token_from_the_wrong_issuer_is_rejected()
    {
        var token = TestTokens.Create(Guid.CreateVersion7().ToString(), issuer: "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeWithRawTokenAsync(token)).StatusCode);
    }

    /// <summary>ValidateAudience: our own token, replayed against a service it was not meant for.</summary>
    [Fact]
    public async Task A_token_for_the_wrong_audience_is_rejected()
    {
        var token = TestTokens.Create(Guid.CreateVersion7().ToString(), audience: "some.other.api");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeWithRawTokenAsync(token)).StatusCode);
    }

    /// <summary>
    /// The scheme is not optional. A raw token with no "Bearer " prefix is not a credential the
    /// handler will look at.
    /// </summary>
    [Fact]
    public async Task The_Bearer_scheme_is_required()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
    }

    /// <summary>
    /// A JWT's payload is base64, not encryption — trivially readable AND trivially editable.
    /// Rewriting role to Admin invalidates the signature, which is the entire point.
    /// </summary>
    [Fact]
    public async Task Editing_the_payload_to_claim_Admin_invalidates_the_token()
    {
        var (_, tokens, _) = await factory.CreateSignedInClientAsync();
        var parts = tokens.AccessToken.Split('.');

        var payload = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        Assert.Contains("\"role\":\"User\"", payload);      // readable, as advertised

        parts[1] = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(
            payload.Replace("\"role\":\"User\"", "\"role\":\"Admin\"")));

        var response = await GetMeWithRawTokenAsync(string.Join('.', parts));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------------

    private async Task<JsonElement> ReadMeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/auth/me");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<HttpResponseMessage> GetMeWithRawTokenAsync(string token)
    {
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/auth/me");
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
