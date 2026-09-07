using dotnetai.Api.Data;
using dotnetai.Api.Endpoints;
using dotnetai.Api.Models;
using dotnetai.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenAI;
using System.ClientModel;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// A named constant rather than a magic string repeated at registration and at use — the same
// reasoning as Roles.Admin. A typo in one of the two spellings would silently apply NO policy
// instead of this one, and you would debug it as a CORS problem.
const string SpaCorsPolicy = "Spa";

// ---------------------------------------------------------------------------
// SERVICES ("the container"). Everything registered here can be injected later.
// ---------------------------------------------------------------------------

builder.Services.AddOpenApi();

// THE OPTIONS PATTERN + FAIL-FAST VALIDATION.
//
//   Bind()                  -> map the "Jwt" config section onto JwtOptions
//   ValidateDataAnnotations -> enforce the [Required]/[MinLength]/[Range] attributes
//   ValidateOnStart()       -> run that validation during startup, NOT lazily on first use
//
// ValidateOnStart is the important one. Without it, options are validated the first time
// something resolves IOptions<JwtOptions> — which would be the first login request, in
// production, at 3am. With it, a bad config refuses to boot and tells you which key is wrong.
builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// EF CORE. AddDbContext registers AppDbContext as SCOPED (one per HTTP request) and wires up
// the SQLite provider. The connection string comes from ConnectionStrings:AppDb in
// appsettings.json — "Data Source=dotnetai.db" is relative to the CONTENT ROOT, so the database
// file lands next to Program.cs.
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("AppDb")));

// PASSWORD HASHING. This is Microsoft's PBKDF2 implementation, borrowed from ASP.NET Core
// Identity without adopting the rest of Identity. Never hand-roll password hashing — the
// choice of algorithm, salt handling, and iteration count are exactly the details that are
// easy to get subtly, silently wrong.
//
// SINGLETON is correct here: PasswordHasher<T> is stateless and its only dependency is
// IOptions<PasswordHasherOptions>, itself a singleton. Contrast with TokenService in Phase 4.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// TOKEN MINTING — and a lesson in DI LIFETIMES.
//
// As written, TokenService depends only on IOptions<JwtOptions>, which is a singleton, so
// Singleton would work today. Register it Scoped anyway, because of what happens the moment
// someone adds AppDbContext to its constructor: a singleton would CAPTURE that scoped
// DbContext and hold it for the entire process lifetime, shared across every concurrent
// request, accumulating tracked entities forever. That is a CAPTIVE DEPENDENCY.
//
// ASP.NET Core catches it for you at builder.Build() — but only in Development, where
// ValidateScopes and ValidateOnBuild default to true. In Production those checks are OFF and
// you get silent data corruption instead of an exception. Never rely on the Development guard.
//
// The rule: a service's lifetime must be no longer than the shortest lifetime among its
// dependencies.
builder.Services.AddScoped<TokenService>();

// ---------------------------------------------------------------------------
// OPENAI — the model behind /chat
// ---------------------------------------------------------------------------
//
// Same options ceremony as Jwt, for the same reason. The difference worth noticing: OpenAI:ApiKey
// is not just a config value you can get wrong, it is a config value that COSTS MONEY when
// someone else gets hold of it. It lives in user-secrets in development and in an environment
// variable or a vault in production — never in appsettings.json, which is committed.
builder.Services
    .AddOptions<OpenAiOptions>()
    .Bind(builder.Configuration.GetSection(OpenAiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// AddChatClient registers IChatClient — the provider-agnostic abstraction from
// Microsoft.Extensions.AI. THIS LINE IS THE ONLY PLACE IN THE APPLICATION THAT KNOWS THE MODEL
// IS OPENAI'S. Endpoints depend on the interface, so switching to Azure OpenAI, Anthropic, or a
// local Ollama is an edit here and nowhere else — and, more usefully day to day, the tests swap
// in a fake and never touch the network.
//
// SINGLETON (AddChatClient's default) is right: the underlying client is thread-safe, holds no
// per-request state, and owns an HttpClient with a connection pool you very much want reused.
// Constructing one per request is the classic socket-exhaustion mistake.
builder.Services.AddChatClient(services =>
{
    var openAi = services.GetRequiredService<IOptions<OpenAiOptions>>().Value;

    return new OpenAIClient(new ApiKeyCredential(openAi.ApiKey))
        .GetChatClient(openAi.Model)
        .AsIChatClient();
});

// ---------------------------------------------------------------------------
// RATE LIMITING — because /chat spends money
// ---------------------------------------------------------------------------
//
// Every other endpoint in this app costs a little CPU when abused. /chat costs dollars, charged
// to you, per call. An authenticated-but-unmetered endpoint in front of a paid API is how a
// single compromised account, or one runaway retry loop in a client, turns into a bill.
//
// PARTITIONED BY USER, not by IP. IP partitioning would put an entire office behind one NAT into
// the same bucket, and would do nothing at all about one account hammering from many addresses.
// The "sub" claim is the right key here precisely because the endpoint requires a token anyway.
builder.Services.AddRateLimiter(limiter =>
{
    // DEFAULTS TO 503 Service Unavailable, which is a lie — the service is fine, the caller is
    // over quota. 429 is the answer that tells a client to back off and retry.
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy(ChatEndpoints.RateLimitPolicy, context =>
    {
        var openAi = context.RequestServices.GetRequiredService<IOptions<OpenAiOptions>>().Value;

        // This reads HttpContext.User, so the middleware MUST run after UseAuthentication —
        // see the pipeline below. Unauthenticated callers never reach here (the endpoint also
        // has RequireAuthorization), but the fallback keeps them out of everyone else's bucket.
        var partitionKey = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = openAi.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),

            // QUEUE NOTHING. Queueing would hold a request open waiting for a window to roll
            // over, and the caller — a browser waiting on a stream — would read that as the app
            // hanging. Refuse immediately and let the UI say so.
            QueueLimit = 0,
        });
    });
});

// ---------------------------------------------------------------------------
// AUTHENTICATION — "who are you?"
// ---------------------------------------------------------------------------
//
// Read the config directly here rather than through IOptions: this runs during registration,
// before the container exists. The Bind/ValidateOnStart above still guards it — a bad key
// stops the app before any of this matters.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
          ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)  // the DEFAULT scheme
    .AddJwtBearer(options =>
    {
        // TURN OFF LEGACY CLAIM REMAPPING. This defaults to TRUE, and when it is on the handler
        // rewrites "sub" -> ClaimTypes.NameIdentifier and "role" -> ClaimTypes.Role (both long
        // XML-namespace URIs from the WS-Federation era). The result: User.FindFirstValue("sub")
        // returns NULL even though "sub" is plainly there in the token, and beginners lose an
        // hour. With it off, what we signed is exactly what we read.
        options.MapInboundClaims = false;

        // THIS OBJECT IS THE SECURITY BOUNDARY OF THE ENTIRE APPLICATION.
        // Every rule for "is this token acceptable?" lives here.
        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Who signed it. Guards against a token minted by some OTHER system that happens
            // to share our signing key.
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,

            // Who it was meant for. Guards against token forwarding — a token we issued for
            // service A being replayed against service B.
            ValidateAudience = true,
            ValidAudience = jwt.Audience,

            // Honour "exp" and "nbf". Defaults to true, but written out because a reader
            // should not have to look up defaults to audit your security boundary.
            ValidateLifetime = true,

            // IssuerSigningKey IS the line that stops forgery. Signature verification runs
            // against whatever key you put here, and a token signed with any other key — or one
            // whose payload was edited after signing — fails. That is what makes a claim like
            // role=Admin trustworthy rather than a suggestion from the client.
            //
            // ValidateIssuerSigningKey is NOT that switch, despite the name. It gates an extra
            // check on the KEY OBJECT itself (for an X509SecurityKey, whether the certificate is
            // within its validity period) and has nothing to do with whether the signature is
            // verified. Set it to false and forged tokens are still rejected — confirmed by
            // mutating this line and watching AuthMeTests stay green. For a symmetric key it is
            // close to a no-op; it is left on because it costs nothing and matters the day this
            // becomes a certificate.
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),

            // ClockSkew DEFAULTS TO 5 MINUTES. It exists because the issuing and validating
            // machines may have drifting clocks, and rejecting a token that is two seconds
            // "early" is worse than accepting one two seconds stale. The consequence nobody
            // warns you about: a 15-minute token is really accepted for ~20 minutes, which
            // makes "watch my token expire" experiments maddening. 5s in dev so expiry is
            // observable; 30-60s in production. NEVER TimeSpan.Zero — the first NTP drift
            // produces spurious 401s.
            ClockSkew = TimeSpan.FromSeconds(5),

            // Because MapInboundClaims is false, nothing rewrites our claim types — so we must
            // tell the framework which RAW claim names carry identity and role.
            //
            // RoleClaimType is load-bearing: without it, User.IsInRole() and RequireRole() look
            // for ClaimTypes.Role (a long URI), find nothing, and EVERY admin check silently
            // fails with a 403 that gives you no hint why.
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role",
        };

        // Echoes the rejection reason in the WWW-Authenticate header ("token expired",
        // "signature key was not found"). Excellent while learning; off in production, where
        // you should not tell an attacker why their token failed.
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    });

// ---------------------------------------------------------------------------
// AUTHORIZATION — "are you allowed?"
// ---------------------------------------------------------------------------
// A named policy, referenced as .RequireAuthorization("AdminOnly") on an endpoint.
// RequireRole is sugar for "the principal has a claim whose TYPE equals RoleClaimType and
// whose VALUE is Admin" — which is why the RoleClaimType setting above matters so much.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin));

// ---------------------------------------------------------------------------
// CORS — "may a page from ANOTHER origin call me?"
// ---------------------------------------------------------------------------
//
// FIRST, THE THING NOBODY TELLS YOU: CORS is enforced by the BROWSER, not by us. curl, Postman
// and your integration tests ignore it entirely, which is why an endpoint can work perfectly in
// every tool you own and still fail from the SPA. The browser makes the request, reads the
// response headers, and refuses to hand the body to JavaScript unless this server said the
// caller's origin is welcome. All we do here is say so.
//
// An ORIGIN is scheme + host + port. http://localhost:5173 and https://localhost:5173 are
// different origins; so are :5173 and :5231. That last one is the whole reason we need this:
// the Vite dev server and this API are two different ports on the same machine, so every fetch
// the SPA makes is cross-origin.
//
// THE PREFLIGHT. Any request carrying "Authorization: Bearer ..." is not a "simple" request, so
// before the real call the browser sends an OPTIONS probe asking whether that header is allowed.
// AllowAnyHeader() is what answers it. Get this wrong and you never see your GET at all —
// only a failed OPTIONS and a console message that names no cause.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy(SpaCorsPolicy, policy => policy
        // NOT AllowAnyOrigin(). Name the origins you trust and keep them in configuration, so
        // production and development differ by a config value rather than by a code branch.
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());

    // WHY NO .AllowCredentials()? Because we authenticate with a Bearer token in a header, not
    // with a cookie. Credentials mode is for cookies, and we deliberately do not use it — see
    // the token-storage note in the SPA's auth-context.jsx.
    //
    // Worth knowing anyway: .AllowAnyOrigin() and .AllowCredentials() together are ILLEGAL and
    // throw at startup. The spec forbids it, because "any site may call me AND the browser
    // should attach the user's cookies" is a CSRF vulnerability with extra steps.
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// PIPELINE (middleware). Order matters enormously here — see Phase 5.
// ---------------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// A 307 REDIRECT AND A CORS PREFLIGHT DO NOT MIX. Browsers refuse to follow redirects on an
// OPTIONS preflight — the request simply fails, and the console blames CORS rather than the
// redirect. Left unguarded, this line breaks every SPA call to the http:// profile on port 5231.
//
// Redirecting is still the right thing in production, where the SPA is served over https and
// no preflight is ever answered with a 307.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// ORDER, AGAIN. UseCors must run BEFORE UseAuthentication/UseAuthorization.
//
// The reason is subtler than the usual telling, so here is what actually breaks. CorsMiddleware
// registers an OnStarting callback to stamp the Access-Control-* headers, THEN calls the rest of
// the pipeline. Middleware that never runs stamps nothing — so if authorization short-circuits
// with a 401 before CORS has been reached, that 401 goes back with no CORS headers on it.
//
// The browser then refuses to let JavaScript read the response at all, and reports an opaque
// CORS error. Your SPA cannot tell "your token expired" from "the server is unreachable", so it
// cannot refresh and retry — it just fails, blaming CORS, and you go and stare at a CORS policy
// that was never the problem.
//
// Note what is NOT the reason: the preflight survives either ordering here. An OPTIONS request
// does not match a MapGet endpoint, so AuthorizationMiddleware finds no endpoint metadata and
// never short-circuits it. It is the REAL request's 401 that loses its headers. CorsTests pins
// exactly that.
app.UseCors(SpaCorsPolicy);

// THESE TWO LINES, IN THIS ORDER. The order is load-bearing.
//
// 1. UseAuthentication() adds AuthenticationMiddleware. It asks the default scheme's handler
//    (JwtBearerHandler) to authenticate: read the "Authorization: Bearer <token>" header,
//    validate it against TokenValidationParameters, and on success build a ClaimsPrincipal
//    and assign it to HttpContext.User. On FAILURE it does not throw and does not
//    short-circuit — it simply leaves HttpContext.User as the anonymous default.
//    Authentication answers "who are you?" and nothing else.
//
// 2. UseAuthorization() adds AuthorizationMiddleware. It reads the matched endpoint's
//    authorization metadata (put there by .RequireAuthorization()), reads HttpContext.User,
//    and evaluates the policy. Authorization answers "are you allowed?"
//
// REVERSE THEM and authorization reads HttpContext.User before anything has populated it.
// It is always the anonymous principal, so EVERY protected endpoint returns 401 even with a
// perfectly valid token — and there is no error message anywhere. That silence is what makes
// this bug so expensive.
//
// Both must run after routing, because authorization needs to know WHICH endpoint matched in
// order to read its metadata. You never call UseRouting() here: WebApplication inserts it at
// the head of the pipeline automatically. (It also auto-inserts these two if you omit them,
// which is why some tutorials appear to work without them. Write them anyway — an explicit
// pipeline is a readable one, and the day you add a third order-sensitive middleware, the
// implicit version will bite.)
app.UseAuthentication();
app.UseAuthorization();

// AFTER authentication, because the /chat policy partitions on the "sub" claim and there is no
// HttpContext.User to read before AuthenticationMiddleware has built one. Move this line above
// UseAuthentication and every caller silently shares the single "anonymous" bucket — the limiter
// still "works", it just meters the wrong thing, and nothing anywhere tells you.
app.UseRateLimiter();

// ---------------------------------------------------------------------------
// ENDPOINTS
// ---------------------------------------------------------------------------

app.MapAuthEndpoints();
app.MapDemoEndpoints();
app.MapChatEndpoints();

app.Run();

// ---------------------------------------------------------------------------
// TEST SEAM
// ---------------------------------------------------------------------------
//
// Top-level statements make the compiler generate a Program class for you, but it is INTERNAL,
// and WebApplicationFactory<TEntryPoint> needs a type it can name from the test assembly.
// Declaring the other half of that partial class here — and declaring it public — is the
// documented way to widen it. There is nothing in the body and there does not need to be:
// the compiler merges this with the generated half that holds Main.
//
// The alternative is [assembly: InternalsVisibleTo("dotnetai.Api.Tests")], which works equally
// well and leaks less. This is the more common spelling, so it is the less surprising one.
public partial class Program;
