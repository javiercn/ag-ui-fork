using AGUI.Client;
using Microsoft.Extensions.AI;

namespace Step07_ThinkingEvents.Client;

public static class SampleClient
{
    public static async Task RunAsync(
        IChatClient chatClient,
        TextWriter output,
        List<List<ChatMessage>>? messagesPerTurn = null,
        List<List<ChatResponseUpdate>>? updatesPerTurn = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is 15 * 23?"),
        };
        messagesPerTurn?.Add(messages.ToList());
        await output.WriteLineAsync("> What is 15 * 23?").ConfigureAwait(false);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            messages, options: null, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent { Text: { Length: > 0 } reasoning }:
                        await output.WriteAsync($"[thinking] {reasoning}").ConfigureAwait(false);
                        await output.WriteLineAsync().ConfigureAwait(false);
                        break;
                    case TextContent { Text: { Length: > 0 } text }:
                        await output.WriteAsync(text).ConfigureAwait(false);
                        break;
                }
            }
        }
        await output.WriteLineAsync().ConfigureAwait(false);
        updatesPerTurn?.Add(updates);
    }
}
