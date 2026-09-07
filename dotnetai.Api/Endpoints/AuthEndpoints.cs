using dotnetai.Api.Data;
using dotnetai.Api.Dtos;
using dotnetai.Api.Models;
using dotnetai.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;

namespace dotnetai.Api.Endpoints;

public static class AuthEndpoints
{
    /// <summary>
    /// MapGroup gives you a RouteGroupBuilder: a common URL prefix and common metadata declared
    /// once instead of repeated on every route. Combined with this extension-method pattern, it
    /// keeps Program.cs readable as a table of contents rather than 250 lines of nested lambdas.
    /// This is the Minimal API answer to "where did my controllers go".
    /// </summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);

        // A window onto exactly what the framework read out of your token. Invaluable for
        // debugging, and the fastest way to see claim mapping in action.
        group.MapGet("/me", Me).RequireAuthorization();

        // NEITHER of these gets .RequireAuthorization(), and that is deliberate.
        // You call /auth/refresh precisely BECAUSE your access token has expired. Requiring a
        // valid access token in order to obtain a valid access token is a deadlock — and it is
        // a mistake people make constantly. The REFRESH TOKEN is the credential here.
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);

        return app;
    }

    /// <summary>
    /// ClaimsPrincipal is bound automatically by Minimal APIs — no HttpContext needed. It IS
    /// HttpContext.User, the object AuthenticationMiddleware built from your token.
    /// </summary>
    private static IResult Me(ClaimsPrincipal user) => TypedResults.Ok(new
    {
        // These work ONLY because MapInboundClaims = false in Program.cs. With the default
        // (true), the handler rewrites "sub" to a long ClaimTypes.NameIdentifier URI and both
        // of these return null. That is the trap that costs beginners an hour.
        Sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub),
        Email = user.FindFirstValue(JwtRegisteredClaimNames.Email),
        Role = user.FindFirstValue("role"),

        // These work because of NameClaimType / RoleClaimType on TokenValidationParameters,
        // which tell the framework which raw claim names carry identity and role.
        Name = user.Identity?.Name,
        IsAdmin = user.IsInRole(Roles.Admin),

        // The raw truth. Look at the Type values: short names, not URIs.
        AllClaims = user.Claims.Select(c => new { c.Type, c.Value }),
    });

    // Note the parameters: Minimal APIs inspect the handler signature and resolve each one.
    // RegisterRequest comes from the JSON body (it's a complex type), while AppDbContext and
    // IPasswordHasher<User> are pulled out of the DI container. You never write "new AppDbContext()"
    // and you never touch a service locator — the framework hands you what you declared you need.
    private static async Task<IResult> RegisterAsync(
        RegisterRequest req,
        AppDbContext db,
        IPasswordHasher<User> hasher)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || req.Password is not { Length: >= 8 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["credentials"] = ["Email is required and password must be at least 8 characters."]
            });
        }

        // NORMALIZE BEFORE COMPARING. Without this you get two accounts for
        // "Jeff@Example.com" and "jeff@example.com", because SQLite's default TEXT collation is
        // case-sensitive and the unique index sees them as different values.
        //
        // ToLowerInvariant, NOT ToLower: ToLower is culture-sensitive, and in Turkish
        // "I".ToLower() is "ı" (dotless i). The same email would then normalize differently
        // depending on the server's locale. That is a real bug that has caused real outages.
        var email = req.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email))
        {
            // Deliberately NOT 409 Conflict. A 409 turns this endpoint into an account
            // enumeration oracle: anyone could test an email list against it and learn who has
            // an account here. That is step one of a credential-stuffing campaign.
            //
            // The honest tradeoff: this is worse UX for someone who forgot they registered.
            // The real-world fix is to always return this generic response AND send an email —
            // "welcome" to new addresses, "someone tried to register with your address, reset
            // your password instead?" to existing ones. The email channel is authenticated, so
            // it can carry the specific information the HTTP response must not.
            return TypedResults.Ok(new MessageResponse(
                "If that email is available, the account has been created."));
        }

        var user = new User { Email = email, Role = Roles.User };
        user.PasswordHash = hasher.HashPassword(user, req.Password);

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The AnyAsync check above is not atomic: two simultaneous registrations for the
            // same email can both pass it before either saves. The unique index is what actually
            // holds the line, and this is where that violation surfaces. Return the same generic
            // message so the race is invisible to the caller.
            return TypedResults.Ok(new MessageResponse(
                "If that email is available, the account has been created."));
        }

        return TypedResults.Ok(new MessageResponse(
            "If that email is available, the account has been created."));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest req,
        AppDbContext db,
        IPasswordHasher<User> hasher,
        TokenService tokens,
        IOptions<JwtOptions> jwtOptions)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);

        // Same 401 for "no such user" as for "wrong password". Any difference between the two
        // is an enumeration oracle, same problem as registration.
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);

        if (result == PasswordVerificationResult.Failed)
        {
            return TypedResults.Unauthorized();
        }

        // Identity's hasher is VERSIONED — the algorithm and iteration count are encoded inside
        // the stored hash. If this hash was written with older, weaker parameters, the hasher
        // tells us so and we rewrite it with current ones. Every user's password security
        // upgrades for free as they log in. You get this only because we have the plaintext
        // right here, at this exact moment, and never again.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            await db.SaveChangesAsync();
        }

        return TypedResults.Ok(await IssueTokenPairAsync(user, db, tokens, jwtOptions.Value));
    }

    /// <summary>
    /// Exchange a refresh token for a brand-new access/refresh pair.
    ///
    /// ROTATION: every successful refresh CONSUMES the presented token and issues a new one.
    /// The old row is stamped revoked and points forward to its successor, forming a chain
    /// T1 -> T2 -> T3. Each link is single-use.
    ///
    /// REUSE DETECTION falls straight out of that. Suppose an attacker steals T2:
    ///   - If the attacker refreshes first, T2 becomes T3 for them; you still hold the dead T2.
    ///   - If you refresh first, T2 becomes T3 for you; the attacker still holds the dead T2.
    /// Either way SOMEONE eventually presents an already-revoked token — and that never happens
    /// in normal operation, because a legitimate client always holds exactly the newest link.
    /// So a revoked-token presentation is proof the chain was duplicated. We cannot tell victim
    /// from thief, so we kill EVERY session for that user and force a password re-auth. The
    /// attacker does not have the password.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        RefreshRequest req,
        AppDbContext db,
        TokenService tokens,
        IOptions<JwtOptions> jwtOptions,
        ILogger<Program> logger)
    {
        var now = DateTime.UtcNow;
        var hash = TokenService.Hash(req.RefreshToken);

        // .Include() eager-loads the related User in the same query (a SQL JOIN). Without it,
        // stored.User would be null, because EF only loads what you ask for.
        var stored = await db.RefreshTokens
            .Include(rt => rt.User)
            .SingleOrDefaultAsync(rt => rt.TokenHash == hash);

        // Never existed, or the row was purged.
        if (stored is null)
        {
            return TypedResults.Unauthorized();
        }

        // ---------- REUSE DETECTION ----------
        if (stored.RevokedAtUtc is not null)
        {
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}. Revoking all sessions.",
                stored.UserId);

            var live = await db.RefreshTokens
                .Where(rt => rt.UserId == stored.UserId && rt.RevokedAtUtc == null)
                .ToListAsync();

            foreach (var t in live)
            {
                t.RevokedAtUtc = now;
                t.RevokedReason = "reuse-detected";
            }

            await db.SaveChangesAsync();
            return TypedResults.Unauthorized();
        }

        if (stored.ExpiresAtUtc <= now)
        {
            return TypedResults.Unauthorized();
        }

        // ---------- ROTATION ----------
        var jwt = jwtOptions.Value;
        var (rawNew, hashNew) = TokenService.CreateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = stored.UserId,
            TokenHash = hashNew,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(jwt.RefreshTokenDays),
        });

        stored.RevokedAtUtc = now;
        stored.RevokedReason = "rotated";
        stored.ReplacedByTokenHash = hashNew;

        // ONE SaveChangesAsync == ONE TRANSACTION. Revoking the old token and inserting the new
        // one either both happen or neither does. Two separate saves could leave a user holding
        // a revoked token with no replacement if the process died in between.
        await db.SaveChangesAsync();

        // Note: the role is re-read from stored.User, fresh from the database. That is how a
        // role change reaches the client — see Phase 7.
        var (accessToken, accessExpires) = tokens.CreateAccessToken(stored.User);

        return TypedResults.Ok(new TokenPairResponse(
            accessToken, accessExpires, rawNew, now.AddDays(jwt.RefreshTokenDays)));
    }

    /// <summary>
    /// Revoke a refresh token.
    ///
    /// BE HONEST ABOUT WHAT THIS DOES NOT DO: the already-issued ACCESS token stays valid until
    /// it expires, up to AccessTokenMinutes + ClockSkew from now. That is the fundamental,
    /// unavoidable tradeoff of stateless JWTs — the server does not consult a database to
    /// validate one, which is exactly why it is fast and exactly why it cannot be un-issued.
    /// Shortening AccessTokenMinutes narrows the window; closing it entirely requires checking
    /// a denylist on every single request, which throws away the reason you chose JWTs.
    /// </summary>
    private static async Task<IResult> LogoutAsync(RefreshRequest req, AppDbContext db)
    {
        var hash = TokenService.Hash(req.RefreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(rt => rt.TokenHash == hash);

        if (stored is { RevokedAtUtc: null })
        {
            stored.RevokedAtUtc = DateTime.UtcNow;
            stored.RevokedReason = "logout";
            await db.SaveChangesAsync();
        }

        // ALWAYS 204, even for a token that does not exist. Logout is idempotent, and it must
        // not become an oracle for "is this token real?".
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Mints a fresh access/refresh pair and persists the refresh token. Shared by login and
    /// refresh so the two paths cannot drift apart.
    /// </summary>
    private static async Task<TokenPairResponse> IssueTokenPairAsync(
        User user, AppDbContext db, TokenService tokens, JwtOptions jwt)
    {
        var now = DateTime.UtcNow;

        var (accessToken, accessExpires) = tokens.CreateAccessToken(user);

        // The RAW refresh token is returned to the caller and never stored. Only its hash
        // goes in the database.
        var (rawRefresh, hashRefresh) = TokenService.CreateRefreshToken();
        var refreshExpires = now.AddDays(jwt.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hashRefresh,
            CreatedAtUtc = now,
            ExpiresAtUtc = refreshExpires,
        });
        await db.SaveChangesAsync();

        return new TokenPairResponse(accessToken, accessExpires, rawRefresh, refreshExpires);
    }
}
