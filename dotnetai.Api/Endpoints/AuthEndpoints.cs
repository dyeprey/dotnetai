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
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapGet("/me", Me).RequireAuthorization();
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);

        return app;
    }

    private static IResult Me(ClaimsPrincipal user) => TypedResults.Ok(new
    {
        Sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub),
        Email = user.FindFirstValue(JwtRegisteredClaimNames.Email),
        Role = user.FindFirstValue("role"),
        Name = user.Identity?.Name,
        IsAdmin = user.IsInRole(Roles.Admin),
        AllClaims = user.Claims.Select(c => new { c.Type, c.Value }),
    });

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

        var email = req.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email))
        {
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

        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);

        if (result == PasswordVerificationResult.Failed)
        {
            return TypedResults.Unauthorized();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            await db.SaveChangesAsync();
        }

        return TypedResults.Ok(await IssueTokenPairAsync(user, db, tokens, jwtOptions.Value));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest req,
        AppDbContext db,
        TokenService tokens,
        IOptions<JwtOptions> jwtOptions,
        ILogger<Program> logger)
    {
        var now = DateTime.UtcNow;
        var hash = TokenService.Hash(req.RefreshToken);

        var stored = await db.RefreshTokens
            .Include(rt => rt.User)
            .SingleOrDefaultAsync(rt => rt.TokenHash == hash);

        if (stored is null)
        {
            return TypedResults.Unauthorized();
        }

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

        await db.SaveChangesAsync();

        var (accessToken, accessExpires) = tokens.CreateAccessToken(stored.User);

        return TypedResults.Ok(new TokenPairResponse(
            accessToken, accessExpires, rawNew, now.AddDays(jwt.RefreshTokenDays)));
    }

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

        return TypedResults.NoContent();
    }

    private static async Task<TokenPairResponse> IssueTokenPairAsync(
        User user, AppDbContext db, TokenService tokens, JwtOptions jwt)
    {
        var now = DateTime.UtcNow;

        var (accessToken, accessExpires) = tokens.CreateAccessToken(user);

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
