using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace dotnetai.Api.Tests;

/// <summary>
/// IChatClient is the reason these tests can exist at all.
///
/// Without an abstraction between the endpoint and OpenAI, testing /chat would mean either
/// calling the real API — slow, non-deterministic, billed, and impossible in CI without handing
/// out a key — or intercepting HTTP with a handler that fakes OpenAI's exact wire format, which
/// tests the shape of somebody else's JSON rather than anything of ours.
///
/// Substituting the interface tests what we actually wrote: the validation, the trimming, the
/// system prompt, and the SSE framing.
/// </summary>
public sealed class EchoChatClient : IChatClient
{
    /// <summary>Streams the last user message back, one word per update, so a test can assert
    /// on both the CONTENT and the fact that it arrived in several frames rather than one.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastPrompt = messages.ToList();

        var lastUserText = LastPrompt.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        foreach (var word in lastUserText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }

        // An update carrying no text at all. The endpoint must skip it rather than emit an empty
        // delta frame — this is the case that pins that behaviour.
        yield return new ChatResponseUpdate(ChatRole.Assistant, "");
    }

    /// <summary>
    /// The exact prompt the endpoint built, captured for assertions. This is how the tests see
    /// that the system prompt was prepended and that long histories were trimmed — neither of
    /// which is visible in the response.
    /// </summary>
    public IReadOnlyList<ChatMessage> LastPrompt { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app only streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// Fails on the first update — the case that proves a mid-stream failure comes back as an SSE
/// "error" event and not as a 500, because by then the 200 is already on the wire.
/// </summary>
public sealed class ThrowingChatClient : IChatClient
{
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield first, so the response really has started before the throw. Throwing before the
        // first update would be a different (and easier) case than the one worth testing.
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Thinking");
        await Task.Yield();

        throw new InvalidOperationException("upstream exploded");
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This app only streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
