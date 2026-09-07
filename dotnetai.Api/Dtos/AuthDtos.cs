namespace dotnetai.Api.Dtos;

/// <summary>
/// DTOs — Data Transfer Objects. These are the shapes that cross the HTTP boundary, and they
/// are deliberately separate from the entities in Models/.
///
/// INBOUND, the split stops mass assignment: RegisterRequest has no Role property, so a client
/// POSTing {"email":"x","password":"y","role":"Admin"} cannot promote itself. Model binding has
/// nowhere to put that value.
///
/// OUTBOUND, the split stops leaks: User carries PasswordHash. If endpoints returned entities,
/// every response would publish it. With DTOs, what the client can see is a deliberate,
/// reviewable decision rather than an accident of your table schema.
///
/// `record` gives value equality and terse syntax; these are immutable data carriers.
/// </summary>

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

/// <summary>Sent to /auth/refresh and /auth/logout. The refresh token IS the credential
/// for those endpoints, which is why neither one requires an access token.</summary>
public sealed record RefreshRequest(string RefreshToken);

public sealed record MessageResponse(string Message);

/// <summary>
/// What a successful login or refresh returns. The client stores both: the access token goes
/// in the Authorization header on every request, the refresh token is used only when the
/// access token expires.
/// </summary>
public sealed record TokenPairResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

/// <summary>A User with PasswordHash deliberately absent. Used by /admin/users —
/// admins do not get to see hashes either.</summary>
public sealed record UserSummary(Guid Id, string Email, string Role, DateTime CreatedAtUtc);
