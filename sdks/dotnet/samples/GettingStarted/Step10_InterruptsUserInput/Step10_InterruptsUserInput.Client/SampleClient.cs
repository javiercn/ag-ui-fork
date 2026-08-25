using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Extensions.AI;

namespace Step10_InterruptsUserInput.Client;

public static class SampleClient
{
    public static async Task RunAsync(
        IChatClient chatClient,
        TextWriter output,
        List<List<ChatMessage>>? messagesPerTurn = null,
        List<List<ChatResponseUpdate>>? updatesPerTurn = null,
        CancellationToken cancellationToken = default)
    {
        // Turn 1: ask to set up the account; the server pauses with an actionable function call.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Please setup my account"),
        };
        messagesPerTurn?.Add(messages.ToList());
        await output.WriteLineAsync("> Please setup my account").ConfigureAwait(false);

        var turn1 = await StreamAsync(chatClient, messages, output, cancellationToken).ConfigureAwait(false);
        updatesPerTurn?.Add(turn1);

        var workflowCall = turn1
            .SelectMany(u => u.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault();
        if (workflowCall is null)
        {
            return;
        }

        // Turn 2: respond with the idiomatic function result. AGUIChatClient recognizes the
        // pending interrupted call and encodes the result as RunAgentInput.Resume[].
        var responseContent = new FunctionResultContent(workflowCall.CallId, "johndoe42");

        var turn2Messages = new List<ChatMessage>(messages)
        {
            new(ChatRole.Assistant, [workflowCall]),
            new(ChatRole.Tool, [responseContent]),
        };
        messagesPerTurn?.Add(turn2Messages.ToList());
        await output.WriteLineAsync("> [user input: johndoe42]").ConfigureAwait(false);

        var turn2 = await StreamAsync(chatClient, turn2Messages, output, cancellationToken).ConfigureAwait(false);
        updatesPerTurn?.Add(turn2);
    }

    private static async Task<List<ChatResponseUpdate>> StreamAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            messages, options: null, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent
                    {
                        RawRepresentation: AGUIInterrupt interrupt,
                    }:
                        await output.WriteLineAsync(
                            $"[interrupt: {interrupt.Reason}] {interrupt.Message}").ConfigureAwait(false);
                        break;
                    case TextContent { Text: { Length: > 0 } text }:
                        await output.WriteAsync(text).ConfigureAwait(false);
                        break;
                }
            }
        }
        await output.WriteLineAsync().ConfigureAwait(false);
        return updates;
    }
}
