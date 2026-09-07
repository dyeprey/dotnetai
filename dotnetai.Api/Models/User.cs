namespace dotnetai.Api.Models;

/// <summary>
/// An ENTITY: a shape that lives in the database. EF Core maps this class to a "Users" table,
/// one property per column.
///
/// Note what is NOT here: no JSON attributes, no validation attributes for HTTP input. This
/// type describes storage. The shapes that cross the HTTP boundary live in Dtos/ — see the
/// comment on PasswordHash for why that separation is not just tidiness.
/// </summary>
public sealed class User
{
    // Guid.CreateVersion7() (.NET 9+) is time-ordered, unlike Guid.NewGuid(). That matters for
    // database indexes: random GUIDs scatter inserts across the whole B-tree, while v7 GUIDs
    // append in order, the way an auto-increment int would.
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Always stored already-normalized (trimmed, lowercased). See AuthEndpoints.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// The output of PasswordHasher&lt;User&gt; — a PBKDF2 hash with its salt and iteration
    /// count encoded inside. The plaintext password is never stored and cannot be recovered.
    ///
    /// THIS is why Dtos/ exists. Return a User from an endpoint and you have just published
    /// every password hash in your database over HTTP.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// A single string column rather than a Roles table. For v1 that is the right call: one
    /// column, one claim in the token, zero joins. Graduate to a real table (or permission
    /// claims) when you have more than two or three roles.
    /// </summary>
    public string Role { get; set; } = Roles.User;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A NAVIGATION PROPERTY. EF Core reads this to infer the one-to-many relationship
    /// (one User has many RefreshTokens) and generates the foreign key for it. It is not
    /// a column — it is how you traverse the relationship in C#.
    /// </summary>
    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

/// <summary>Role names as constants, so a typo is a compile error rather than a silent 403.</summary>
public static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
}
