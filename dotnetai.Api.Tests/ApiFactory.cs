using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using dotnetai.Api.Data;
using dotnetai.Api.Dtos;
using dotnetai.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace dotnetai.Api.Tests;

/// <summary>
/// Boots the REAL application in memory — real middleware pipeline, real DI container, real
/// endpoint routing, real JWT validation — and hands out HttpClients that talk to it without a
/// socket ever being opened.
///
/// WHY INTEGRATION TESTS AND NOT UNIT TESTS FOR THESE ENDPOINTS: almost everything worth
/// asserting about this API is a property of the PIPELINE, not of a handler. That
/// UseAuthentication runs before UseAuthorization; that a missing token produces 401 and a
/// wrong role produces 403; that MapInboundClaims = false leaves "sub" spelled "sub"; that the
/// unique index actually stops a duplicate registration. Call the handler methods directly and
/// every one of those goes untested, because none of them live in the handler.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Long enough to satisfy JwtOptions' [MinLength(32)] and HMAC-SHA256's 256-bit minimum.</summary>
    public const string JwtKey = "test-signing-key-not-a-secret-000000000000000000";
    public const string Issuer = "dotnetai.Api";
    public const string Audience = "dotnetai.Client";
    public const string AllowedOrigin = "http://localhost:5173";

    /// <summary>Never used to call anything — IChatClient is replaced below. It exists only
    /// to satisfy OpenAiOptions' [Required] + ValidateOnStart, which would otherwise stop the
    /// host from booting on a machine with no OpenAI key (i.e. on CI).</summary>
    public const string OpenAiApiKey = "test-openai-key-not-a-secret";

    /// <summary>Matches the API's serializer: camelCase, case-insensitive.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Each factory gets its OWN database file, so test classes running in parallel cannot see
    // one another's users. A shared file would make "how many users exist?" assertions depend on
    // which other tests happened to be running.
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"dotnetai-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development matters here, and not only for error details: Program.cs skips
        // UseHttpsRedirection outside Production, and a 307 to https would turn every test into
        // an assertion about redirects.
        builder.UseEnvironment("Development");

        // These are appended AFTER the app's own configuration sources, so they win — including
        // over the developer's user-secrets, which hold the real Jwt:Key. Tests must not depend
        // on a machine-local secret that CI does not have.
        builder.UseSetting("ConnectionStrings:AppDb", $"Data Source={_databasePath}");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("Jwt:AccessTokenMinutes", "15");
        builder.UseSetting("Jwt:RefreshTokenDays", "14");
        builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin);

        builder.UseSetting("OpenAI:ApiKey", OpenAiApiKey);
        builder.UseSetting("OpenAI:Model", "test-model");
        builder.UseSetting("OpenAI:SystemPrompt", SystemPrompt);
        builder.UseSetting("OpenAI:MaxHistoryMessages", MaxHistoryMessages.ToString());
        builder.UseSetting("OpenAI:MaxMessageCharacters", MaxMessageCharacters.ToString());

        // Generous enough that no test trips it by accident. The one test that WANTS a 429 sets
        // its own limit — see ChatRateLimitTests.
        builder.UseSetting("OpenAI:RequestsPerMinute", "1000");

        // ConfigureTestServices runs AFTER the application's own registrations, so this is the
        // seam for replacing one. RemoveAll rather than a bare Add: the container returns the
        // LAST registration for a service type, so an Add alone would work — right up until
        // something resolves IEnumerable<IChatClient> and quietly gets both.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(Chat);
        });
    }

    /// <summary>The stand-in for OpenAI. Tests read <c>factory.Chat.LastPrompt</c> to assert on
    /// what the endpoint actually sent to the model.</summary>
    public EchoChatClient Chat { get; } = new();

    public const string SystemPrompt = "You are a test fixture.";
    public const int MaxHistoryMessages = 6;
    public const int MaxMessageCharacters = 200;

    /// <summary>
    /// Runs once, when the host is first built. Migrate() rather than EnsureCreated() so the
    /// schema under test is the one the migrations actually produce — including the unique index
    /// on Email that several tests lean on.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();

        return host;
    }

    /// <summary>
    /// An HttpClient that does NOT follow redirects. The default follows them silently, which
    /// would hide a misconfigured pipeline behind a 200.
    /// </summary>
    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    // ---------------------------------------------------------------------
    // DATABASE ACCESS FOR ARRANGE / ASSERT
    // ---------------------------------------------------------------------

    /// <summary>
    /// Runs work against a FRESH DbContext scope. Fresh matters: reusing the context an endpoint
    /// used would read entities out of its change tracker, so a test could "pass" against values
    /// that were never written to the database.
    /// </summary>
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// Creates a user directly, using the SAME password hasher the app uses, so the account can
    /// actually log in afterwards. Going through /auth/register instead would work for User
    /// accounts but cannot produce an Admin — nothing in the HTTP surface can, by design.
    /// </summary>
    public async Task<User> SeedUserAsync(string email, string password, string role = Roles.User)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var user = new User { Email = email.Trim().ToLowerInvariant(), Role = role };
        user.PasswordHash = hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    // ---------------------------------------------------------------------
    // AUTHENTICATED CLIENTS
    // ---------------------------------------------------------------------

    public async Task<TokenPairResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenPairResponse>(Json))!;
    }

    /// <summary>Seeds a user, logs in, and returns a client with the bearer token already attached.</summary>
    public async Task<(HttpClient Client, TokenPairResponse Tokens, User User)> CreateSignedInClientAsync(
        string? email = null, string password = "correct-horse-battery", string role = Roles.User)
    {
        email ??= UniqueEmail();

        var user = await SeedUserAsync(email, password, role);
        var client = CreateApiClient();
        var tokens = await LoginAsync(client, email, password);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, tokens, user);
    }

    /// <summary>
    /// A fresh address per test. Test classes share one database, so a hard-coded address would
    /// make tests pass or fail depending on the order they ran in.
    /// </summary>
    public static string UniqueEmail(string prefix = "user") =>
        $"{prefix}-{Guid.NewGuid():N}@example.com";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        // The host is torn down by now, so nothing holds the file open.
        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
