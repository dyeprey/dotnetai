using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using dotnetai.Api.Dtos;
using dotnetai.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace dotnetai.Api.Endpoints;

/// <summary>
/// The chat endpoint: take a transcript, forward it to OpenAI, and stream the reply back token
/// by token.
///
/// Note what this file does NOT contain: any mention of OpenAI's HTTP API, its request shape, or
/// its SDK types. It depends on <see cref="IChatClient"/> — the Microsoft.Extensions.AI
/// abstraction — which is what makes the provider a line of registration in Program.cs and makes
/// the tests able to substitute a fake without a network.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>Named so the policy registered in Program.cs and the policy used here cannot drift.</summary>
    public const string RateLimitPolicy = "chat";

    // The SSE "event:" names. A client switches on these to tell prose from failure.
    private const string DeltaEvent = "delta";
    private const string ErrorEvent = "error";
    private const string DoneEvent = "done";

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/chat", ChatAsync)
           .WithName("Chat")
           .WithTags("Chat")
           .RequireAuthorization()
           .RequireRateLimiting(RateLimitPolicy);

        return app;
    }

    private static IResult ChatAsync(
        ChatRequest request,
        IChatClient chat,
        IOptions<OpenAiOptions> options,
        ILogger<ChatLog> logger,
        CancellationToken cancellationToken)
    {
        var config = options.Value;

        // ---------------------------------------------------------------------------
        // VALIDATE BEFORE YOU STREAM. This is the one ordering rule of this endpoint.
        //
        // The moment we return a ServerSentEvents result, the response is committed: status 200
        // and headers are on the wire, and NOTHING after that can turn it into a 400. So every
        // check that could reject the request has to happen here, in the part of the handler
        // that can still return a status code.
        // ---------------------------------------------------------------------------
        var messages = request.Messages ?? [];

        if (messages.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["messages"] = ["Send at least one message."]
            });
        }

        if (messages.Any(m => string.IsNullOrWhiteSpace(m.Text)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["messages"] = ["Messages cannot be empty."]
            });
        }

        if (messages.Any(m => m.Text.Length > config.MaxMessageCharacters))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["messages"] = [$"A single message may not exceed {config.MaxMessageCharacters} characters."]
            });
        }

        // ---------------------------------------------------------------------------
        // BUILD THE PROMPT
        // ---------------------------------------------------------------------------

        // TRIM FROM THE FRONT, NOT THE BACK. The newest turns are the ones the model needs; the
        // opening of a long conversation is the cheapest thing to forget. Doing this server-side
        // rather than trusting the client is the point — the client is where the cost pressure
        // ISN'T, so a bug (or a hostile caller) there would arrive here as a 200-message prompt
        // and a bill to match.
        var recent = messages.Count > config.MaxHistoryMessages
            ? messages.Skip(messages.Count - config.MaxHistoryMessages).ToList()
            : messages;

        // The system prompt is prepended HERE, from configuration — never taken from the client.
        var prompt = new List<ChatMessage>(recent.Count + 1)
        {
            new(ChatRole.System, config.SystemPrompt),
        };

        // Anything the client did not label "assistant" is treated as user input. Defaulting the
        // unknown case to the LOWEST-trust role is the safe direction to fail: a mislabelled turn
        // becomes user text (harmless) rather than words the model believes it said itself.
        prompt.AddRange(recent.Select(m => new ChatMessage(
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User,
            m.Text)));

        // TypedResults.ServerSentEvents (new in .NET 10) sets Content-Type: text/event-stream,
        // disables response buffering, and formats each SseItem into the "event:/data:" frames
        // the wire format expects. Before this you wrote that loop by hand and got the flushing
        // wrong.
        return TypedResults.ServerSentEvents(
            StreamReplyAsync(chat, prompt, logger, cancellationToken));
    }

    /// <summary>
    /// Pulls updates off the model and turns each one into an SSE frame.
    ///
    /// THE AWKWARD SHAPE OF THIS LOOP IS DELIBERATE. C# forbids `yield return` inside a
    /// try/catch, and we need both: we are enumerating something that can throw halfway through
    /// (the network to OpenAI), and we must still emit a frame telling the browser about it.
    /// Hence the manual enumerator and the flags — catch into a variable, then yield after the
    /// catch block has closed. The obvious `try { await foreach (...) yield return ...; }` does
    /// not compile.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<ChatStreamEvent>> StreamReplyAsync(
        IChatClient chat,
        List<ChatMessage> prompt,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var updates = chat
            .GetStreamingResponseAsync(prompt, options: null, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate? update = null;
            string? failure = null;
            var cancelled = false;

            try
            {
                if (await updates.MoveNextAsync())
                {
                    update = updates.Current;
                }
            }
            catch (OperationCanceledException)
            {
                // The user hit Stop, or navigated away, and ASP.NET Core cancelled
                // HttpContext.RequestAborted. There is nobody left to send a frame to.
                cancelled = true;
            }
            catch (Exception exception)
            {
                // A bad API key, a rate limit at OpenAI's end, an outage, a model name that does
                // not exist. LOG the specific cause; SEND a generic one. The exception text can
                // name internals (and, on an auth failure, hint at key state) and the browser is
                // not a place to publish either.
                logger.LogError(exception, "Chat completion failed.");
                failure = "The assistant is unavailable right now. Please try again.";
            }

            if (cancelled)
            {
                yield break;
            }

            if (failure is not null)
            {
                // WHY AN ERROR *EVENT* AND NOT A 500. The response became a 200 the instant the
                // first byte left, so mid-stream failure has no status code to live in. Every
                // streaming API needs an in-band way to say "that went wrong", and this is ours.
                yield return new SseItem<ChatStreamEvent>(new ChatStreamEvent(failure), ErrorEvent);
                yield break;
            }

            if (update is null)
            {
                break;  // the model finished normally
            }

            // Updates arrive for non-text reasons too (role announcements, usage totals, finish
            // reasons). Only text is worth a frame.
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return new SseItem<ChatStreamEvent>(new ChatStreamEvent(text), DeltaEvent);
            }
        }

        // An explicit terminator. A client cannot otherwise distinguish "the model finished" from
        // "the connection died", because both look like a closed stream — and the difference is
        // whether it should show the answer or an error.
        yield return new SseItem<ChatStreamEvent>(new ChatStreamEvent(string.Empty), DoneEvent);
    }

    /// <summary>
    /// A category for ILogger&lt;T&gt;, so chat failures log under "dotnetai.Api.Endpoints.ChatLog"
    /// rather than under a static class you cannot name as a type argument.
    /// </summary>
    internal sealed class ChatLog;
}
