using dotnetai.Api.Data;
using dotnetai.Api.Dtos;
using dotnetai.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Endpoints;

/// <summary>
/// The endpoints that demonstrate the three access levels: anonymous, authenticated, and
/// authenticated-with-a-role.
/// </summary>
public static class DemoEndpoints
{
    private static readonly string[] Summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild",
        "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        // ANONYMOUS. No RequireAuthorization, so anyone can reach it.
        app.MapGet("/", () => "Hello World!");

        // AUTHENTICATED. RequireAuthorization() attaches authorization metadata to this
        // endpoint. AuthorizationMiddleware reads that metadata, looks at HttpContext.User,
        // and short-circuits with 401 if nobody is signed in — the handler below never runs.
        app.MapGet("/weatherforecast", () =>
            Enumerable.Range(1, 5).Select(i => new WeatherForecast(
                DateOnly.FromDateTime(DateTime.Now.AddDays(i)),
                Random.Shared.Next(-20, 55),
                Summaries[Random.Shared.Next(Summaries.Length)])).ToArray())
           .WithName("GetWeatherForecast")
           .RequireAuthorization();

        // AUTHENTICATED **AND** IN A ROLE. Same middleware, stricter policy: a signed-in
        // non-admin gets 403 here, not 401. See the table in Program.cs.
        //
        // Note the projection to UserSummary. Returning db.Users directly would put every
        // PasswordHash on the wire — admins do not get to see those either.
        app.MapGet("/admin/users", async (AppDbContext db) =>
                await db.Users
                    .Select(u => new UserSummary(u.Id, u.Email, u.Role, u.CreatedAtUtc))
                    .ToListAsync())
           .WithName("AdminListUsers")
           .RequireAuthorization("AdminOnly");

        return app;
    }
}

public sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
