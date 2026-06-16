using AGUI.Client;
using Microsoft.Extensions.AI;

namespace Step11_Serialization.Client;

public static class SampleClient
{
    public static async Task RunAsync(
        IChatClient chatClient,
        TextWriter output,
        List<List<ChatMessage>>? messagesPerTurn = null,
        List<List<ChatResponseUpdate>>? updatesPerTurn = null,
        CancellationToken cancellationToken = default)
    {
        // Turn 1: introductory question.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello, tell me about serialization"),
        };
        messagesPerTurn?.Add(messages.ToList());
        await output.WriteLineAsync("> Hello, tell me about serialization").ConfigureAwait(false);

        var turn1 = await StreamAsync(chatClient, messages, options: null, output, cancellationToken).ConfigureAwait(false);
        updatesPerTurn?.Add(turn1);

        // Turn 2: follow-up that references the previous run via parentRunId; only the new
        // message goes on the wire. The server reconstructs the combined history from the
        // run lineage encoded in agui_parent_run_id.
        var conversationId = turn1.FirstOrDefault(u => u.ConversationId != null)?.ConversationId;
        var parentRunId = turn1.FirstOrDefault(u => u.ResponseId != null)?.ResponseId;

        var followUp = new List<ChatMessage>
        {
            new(ChatRole.User, "Tell me more about event compaction"),
        };
        var followUpOptions = new ChatOptions
        {
            ConversationId = conversationId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["agui_parent_run_id"] = parentRunId ?? string.Empty,
            },
        };
        messagesPerTurn?.Add(followUp.ToList());
        await output.WriteLineAsync("> Tell me more about event compaction").ConfigureAwait(false);

        var turn2 = await StreamAsync(chatClient, followUp, followUpOptions, output, cancellationToken).ConfigureAwait(false);
        updatesPerTurn?.Add(turn2);
    }

    private static async Task<List<ChatResponseUpdate>> StreamAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions? options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
            {
                await output.WriteAsync(update.Text).ConfigureAwait(false);
            }
        }
        await output.WriteLineAsync().ConfigureAwait(false);
        return updates;
    }
}
