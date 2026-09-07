using System.Net;
using System.Net.Http.Json;

namespace dotnetai.Api.Tests;

/// <summary>
/// CORS is enforced by the BROWSER, not by us — curl and these tests can reach every endpoint
/// regardless. What the server controls is the HEADERS it sends back, so that is what to assert:
/// not "was the request blocked" but "did we tell the browser to allow it".
/// </summary>
public sealed class CorsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string AllowOrigin = "Access-Control-Allow-Origin";

    /// <summary>
    /// THE MIDDLEWARE-ORDER TEST — the one that actually fails when UseCors is misplaced.
    ///
    /// CorsMiddleware stamps its headers via an OnStarting callback and then calls the rest of
    /// the pipeline. Middleware that never runs stamps nothing, so moving app.UseCors() after
    /// UseAuthorization means this 401 — produced by the authorization middleware, which
    /// short-circuits — comes back with no Access-Control-Allow-Origin on it.
    ///
    /// The browser then refuses to hand the response to JavaScript and reports an opaque CORS
    /// error, so the SPA cannot tell "token expired" from "server unreachable" and cannot
    /// refresh and retry. Verified by mutation: reorder the two lines and this test goes red.
    /// </summary>
    [Fact]
    public async Task A_401_from_the_auth_middleware_still_carries_cors_headers()
    {
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.AllowedOrigin);

        var response = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains(AllowOrigin),
            $"The 401 carried no {AllowOrigin} header — app.UseCors() is probably running " +
            "after app.UseAuthorization().");
        Assert.Equal(ApiFactory.AllowedOrigin, response.Headers.GetValues(AllowOrigin).Single());
    }

    /// <summary>
    /// The preflight is answered, not rejected. Worth pinning, but note that it survives BOTH
    /// middleware orderings: an OPTIONS request does not match a MapGet endpoint, so
    /// AuthorizationMiddleware finds no metadata and never short-circuits it. The test above is
    /// the one that catches a misordered pipeline.
    /// </summary>
    [Fact]
    public async Task Preflight_on_a_protected_endpoint_is_answered_not_rejected()
    {
        var response = await PreflightAsync("/auth/me", ApiFactory.AllowedOrigin);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiFactory.AllowedOrigin, response.Headers.GetValues(AllowOrigin).Single());
    }

    /// <summary>AllowAnyHeader() is what lets the browser send "Authorization" on the real call.</summary>
    [Fact]
    public async Task Preflight_permits_the_Authorization_header()
    {
        var response = await PreflightAsync("/auth/me", ApiFactory.AllowedOrigin);

        var allowed = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("authorization", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unlisted_origin_gets_no_cors_headers()
    {
        var response = await PreflightAsync("/auth/me", "http://evil.example.com");

        // No Allow-Origin header means the browser refuses to hand the response to JavaScript.
        // That is what "blocked by CORS" actually is.
        Assert.False(response.Headers.Contains(AllowOrigin));
    }

    [Fact]
    public async Task A_real_request_from_the_allowed_origin_is_marked_allowed()
    {
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Add("Origin", ApiFactory.AllowedOrigin);

        var response = await client.PostAsJsonAsync("/auth/login",
            new { email = ApiFactory.UniqueEmail(), password = "wrong-but-irrelevant" });

        // Even a 401 must carry the header — otherwise the SPA cannot read the failure and
        // reports an opaque CORS error instead of "incorrect password".
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiFactory.AllowedOrigin, response.Headers.GetValues(AllowOrigin).Single());
    }

    [Fact]
    public async Task A_real_request_from_an_unlisted_origin_is_not_marked_allowed()
    {
        var client = factory.CreateApiClient();
        client.DefaultRequestHeaders.Add("Origin", "http://evil.example.com");

        var response = await client.GetAsync("/");

        Assert.False(response.Headers.Contains(AllowOrigin));
    }

    /// <summary>
    /// Vary: Origin tells caches that the response depends on the request's Origin. Without it a
    /// shared cache can serve the allowed origin's headers to a different origin, which quietly
    /// undoes the whole policy.
    /// </summary>
    [Fact]
    public async Task Responses_vary_on_origin()
    {
        var response = await PreflightAsync("/auth/me", ApiFactory.AllowedOrigin);

        Assert.Contains(response.Headers.GetValues("Vary"),
            v => v.Contains("Origin", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// We authenticate with a bearer header, not a cookie, so credentials mode is deliberately
    /// OFF. It matters: .AllowAnyOrigin() together with .AllowCredentials() is forbidden by the
    /// spec and throws at startup, because "any site may call me AND the browser should attach
    /// the user's cookies" is CSRF with extra steps.
    /// </summary>
    [Fact]
    public async Task Credentials_are_not_enabled()
    {
        var response = await PreflightAsync("/auth/me", ApiFactory.AllowedOrigin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    private async Task<HttpResponseMessage> PreflightAsync(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        return await factory.CreateApiClient().SendAsync(request);
    }
}
