using System.Text.Json;
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
        // Turn 1: setup request — server emits a user-input interrupt asking for a username.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Please setup my account"),
        };
        messagesPerTurn?.Add(messages.ToList());
        await output.WriteLineAsync("> Please setup my account").ConfigureAwait(false);

        var turn1 = await StreamAsync(chatClient, messages, options: null, output, cancellationToken).ConfigureAwait(false);
        updatesPerTurn?.Add(turn1);

        // Turn 2: provide the requested input via state and resume.
        var turn2Messages = new List<ChatMessage>(messages)
        {
            new(ChatRole.Assistant, "I need some additional information to complete the setup."),
        };

        var resumeState = new
        {
            interruptResponse = new
            {
                response = "johndoe42",
                prompt = "Please enter your preferred username:",
            },
        };
        var turn2Options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                State = JsonSerializer.SerializeToElement(resumeState),
            },
        };

        messagesPerTurn?.Add(turn2Messages.ToList());
        await output.WriteLineAsync("> [user input: johndoe42]").ConfigureAwait(false);

        var turn2 = await StreamAsync(chatClient, turn2Messages, turn2Options, output, cancellationToken).ConfigureAwait(false);
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
