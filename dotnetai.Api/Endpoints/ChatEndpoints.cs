using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using dotnetai.Api.Dtos;
using dotnetai.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace dotnetai.Api.Endpoints;

public static class ChatEndpoints
{
    public const string RateLimitPolicy = "chat";

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

        var recent = messages.Count > config.MaxHistoryMessages
            ? messages.Skip(messages.Count - config.MaxHistoryMessages).ToList()
            : messages;

        var prompt = new List<ChatMessage>(recent.Count + 1)
        {
            new(ChatRole.System, config.SystemPrompt),
        };

        prompt.AddRange(recent.Select(m => new ChatMessage(
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User,
            m.Text)));

        return TypedResults.ServerSentEvents(
            StreamReplyAsync(chat, prompt, logger, cancellationToken));
    }

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
                cancelled = true;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Chat completion failed.");
                failure = "The assistant is unavailable right now. Please try again.";
            }

            if (cancelled)
            {
                yield break;
            }

            if (failure is not null)
            {
                yield return new SseItem<ChatStreamEvent>(new ChatStreamEvent(failure), ErrorEvent);
                yield break;
            }

            if (update is null)
            {
                break;
            }

            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return new SseItem<ChatStreamEvent>(new ChatStreamEvent(text), DeltaEvent);
            }
        }

        yield return new SseItem<ChatStreamEvent>(new ChatStreamEvent(string.Empty), DoneEvent);
    }

    internal sealed class ChatLog;
}
