namespace dotnetai.Api.Dtos;

public sealed record ChatMessageDto(string Role, string Text);

public sealed record ChatRequest(IReadOnlyList<ChatMessageDto>? Messages);

public sealed record ChatStreamEvent(string Text);
