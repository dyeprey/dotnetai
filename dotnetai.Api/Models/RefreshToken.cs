namespace dotnetai.Api.Models;

/// <summary>
/// One row per issued refresh token.
///
/// WHY THIS TABLE EXISTS AT ALL: a JWT is validated purely from its signature — the server
/// never touches the database. That is what makes JWTs fast and horizontally scalable, and it
/// is exactly why a JWT CANNOT BE REVOKED. Once signed, it is valid until it expires, full stop.
///
/// So we pair a short-lived, un-revocable access token with a long-lived refresh token that is
/// nothing but a row in this table. Deleting or flagging the row revokes it instantly. The
/// refresh token's only power is to mint new access tokens.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;   // set by EF when you .Include() it

    /// <summary>
    /// SHA-256 (hex) of the raw token. The raw value goes to the client exactly once and is
    /// never stored, so a leaked database backup yields useless hashes rather than working
    /// sessions for every user. Treat a refresh token as what it is: a bearer credential,
    /// same class as a password.
    /// </summary>
    public string TokenHash { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Null means still live. Non-null means this token has been spent or killed.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>"rotated" | "logout" | "reuse-detected" — useful when reading the audit trail.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>
    /// Forward link in the rotation chain: T1 -> T2 -> T3. Every refresh consumes the presented
    /// token and issues a new one, so each link is single-use. That single-use property is what
    /// makes reuse detection possible — see AuthEndpoints.RefreshAsync.
    /// </summary>
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}
