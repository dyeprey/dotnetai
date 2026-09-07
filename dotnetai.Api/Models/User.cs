namespace dotnetai.Api.Models;

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string Role { get; set; } = Roles.User;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

public static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
}
