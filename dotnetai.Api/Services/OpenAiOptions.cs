using System.ComponentModel.DataAnnotations;

namespace dotnetai.Api.Services;

/// <summary>
/// Strongly-typed view of the "OpenAI" section, bound and validated exactly like
/// <see cref="JwtOptions"/> — same pattern, and for the same reason: a typo in a config key
/// should stop the app at startup with a message that names the key, not produce a 500 on the
/// first message a user sends.
/// </summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// The API key. NOT in appsettings.json — that file is committed, and a leaked OpenAI key is
    /// somebody else spending your money. Development reads it from user-secrets; production from
    /// an environment variable (OpenAI__ApiKey) or a vault. Same story as Jwt:Key.
    ///
    /// [Required] plus ValidateOnStart means the app REFUSES TO BOOT without one. That is
    /// deliberate: chat is the only thing this app does, so an instance with no key is not a
    /// degraded app, it is a broken one, and finding that out at startup beats finding out from
    /// the first user.
    /// </summary>
    [Required(ErrorMessage =
        "OpenAI:ApiKey is missing. Set it with: " +
        "dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"")]
    [MinLength(1, ErrorMessage = "OpenAI:ApiKey must not be empty.")]
    public string ApiKey { get; init; } = "";

    /// <summary>
    /// Which model to call. A plain config value on purpose — swapping models is the single most
    /// common change you will make here, and it should never require a recompile.
    /// </summary>
    [Required, MinLength(1)]
    public string Model { get; init; } = "gpt-4o-mini";

    /// <summary>
    /// Prepended to every conversation as the system message. Config rather than a constant so
    /// you can retune the assistant's behaviour without shipping code.
    /// </summary>
    [Required, MinLength(1)]
    public string SystemPrompt { get; init; } = "You are a helpful assistant.";

    /// <summary>
    /// How many turns of history the client may send back. This is a COST CONTROL, not a
    /// nicety: the whole conversation is re-sent on every message, so tokens billed per request
    /// grow with the length of the chat. Past this many messages the oldest are dropped.
    /// </summary>
    [Range(2, 200)]
    public int MaxHistoryMessages { get; init; } = 40;

    /// <summary>Rejects a single oversized message before it reaches the (metered) API.</summary>
    [Range(1, 100_000)]
    public int MaxMessageCharacters { get; init; } = 8_000;

    /// <summary>
    /// Requests per minute, per signed-in user. See the rate limiter in Program.cs for why an
    /// endpoint that spends money on your behalf must not be left unmetered.
    /// </summary>
    [Range(1, 1000)]
    public int RequestsPerMinute { get; init; } = 20;
}
