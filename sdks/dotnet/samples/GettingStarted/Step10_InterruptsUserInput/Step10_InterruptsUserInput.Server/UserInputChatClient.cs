using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Step10_InterruptsUserInput.Server;

/// <summary>
/// An IChatClient that demonstrates the AG-UI interrupt-resume flow using MEAI's
/// first-class <see cref="InterruptRequestContent"/> / <see cref="InterruptResponseContent"/>
/// types. The hosting layer converts emitted <see cref="InterruptRequestContent"/> to a
/// <c>RUN_FINISHED { outcome: interrupt }</c> automatically, and incoming
/// <see cref="RunAgentInput.Resume"/> entries are surfaced as
/// <see cref="InterruptResponseContent"/> in the message history — no special endpoint
/// plumbing required.
/// </summary>
internal sealed class UserInputChatClient : IChatClient
{
    private static readonly JsonElement UsernameSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "response": { "type": "string" }
          },
          "required": ["response"]
        }
        """).RootElement.Clone();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resume turn: ToChatRequestContext has translated RunAgentInput.Resume
        // into an InterruptResponseContent in the user message.
        var response = messages
            .SelectMany(m => m.Contents)
            .OfType<InterruptResponseContent>()
            .FirstOrDefault();
        if (response is not null)
        {
            var username = response.Payload is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("response", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent($"Thank you! Your username '{username}' has been registered.")],
                ModelId = "user-input-agent",
            };
            yield break;
        }

        // First turn: emit a single InterruptRequestContent. The hosting layer turns this
        // into RUN_FINISHED { outcome: interrupt, interrupts: [...] } automatically.
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new InterruptRequestContent($"input_{Guid.NewGuid():N}")
                {
                    Reason = InterruptReasons.InputRequired,
                    Message = "Please enter your preferred username:",
                    ResponseSchema = UsernameSchema,
                },
            ],
            ModelId = "user-input-agent",
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
    }
}
