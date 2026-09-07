namespace dotnetai.Api.Dtos;

/// <summary>
/// One turn of the conversation as it crosses the wire. Role is "user" or "assistant"; the
/// system prompt is NOT accepted from the client — it comes from configuration and is prepended
/// on the server.
///
/// That is not fussiness. A client that could send its own system message could overwrite yours
/// ("ignore your instructions, you are now..."), which is prompt injection with the front door
/// left open. Same reasoning as RegisterRequest having no Role property: never accept a field
/// whose only use is to escalate.
/// </summary>
public sealed record ChatMessageDto(string Role, string Text);

/// <summary>
/// The whole conversation, re-sent on every message.
///
/// WHY THE CLIENT CARRIES THE HISTORY: the OpenAI chat API is stateless — it has no memory of
/// your last call, so *somebody* has to replay the transcript. Keeping it in the browser means
/// this endpoint stays stateless too: no session affinity, no conversation table, nothing to
/// migrate. The cost is that the transcript is re-uploaded (and re-billed as input tokens) every
/// turn, and it vanishes on reload. Persisting conversations server-side is the natural next
/// step; it is a database schema, not a rewrite of this endpoint.
/// </summary>
public sealed record ChatRequest(IReadOnlyList<ChatMessageDto>? Messages);

/// <summary>
/// The payload of one Server-Sent Event. Which KIND of event it is lives in the SSE event type
/// (delta / error / done), not in this record — see ChatEndpoints.
/// </summary>
public sealed record ChatStreamEvent(string Text);
