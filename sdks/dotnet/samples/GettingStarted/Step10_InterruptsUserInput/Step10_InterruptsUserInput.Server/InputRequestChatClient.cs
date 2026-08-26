using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Step10_InterruptsUserInput.Server;

internal sealed class InputRequestChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    update.RawRepresentation = new InputRequestEvent(
                        call.CallId,
                        Program.ExtractPrompt(call.Arguments));
                    break;
                }
            }

            yield return update;
        }
    }
}
