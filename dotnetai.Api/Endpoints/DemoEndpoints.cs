using dotnetai.Api.Data;
using dotnetai.Api.Dtos;
using dotnetai.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Endpoints;

public static class DemoEndpoints
{
    private static readonly string[] Summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild",
        "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => "Hello World!");

        app.MapGet("/weatherforecast", () =>
            Enumerable.Range(1, 5).Select(i => new WeatherForecast(
                DateOnly.FromDateTime(DateTime.Now.AddDays(i)),
                Random.Shared.Next(-20, 55),
                Summaries[Random.Shared.Next(Summaries.Length)])).ToArray())
           .WithName("GetWeatherForecast")
           .RequireAuthorization();

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
