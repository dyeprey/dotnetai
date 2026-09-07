using System.ComponentModel.DataAnnotations;

namespace dotnetai.Api.Services;

public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    [Required(ErrorMessage =
        "OpenAI:ApiKey is missing. Set it with: " +
        "dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"")]
    [MinLength(1, ErrorMessage = "OpenAI:ApiKey must not be empty.")]
    public string ApiKey { get; init; } = "";

    [Required, MinLength(1)]
    public string Model { get; init; } = "gpt-4o-mini";

    [Required, MinLength(1)]
    public string SystemPrompt { get; init; } = "You are a helpful assistant.";

    [Range(2, 200)]
    public int MaxHistoryMessages { get; init; } = 40;

    [Range(1, 100_000)]
    public int MaxMessageCharacters { get; init; } = 8_000;

    [Range(1, 1000)]
    public int RequestsPerMinute { get; init; } = 20;
}
