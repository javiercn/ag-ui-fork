using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

internal sealed class ServerCallCapture
{
    internal ServerCallCapture(RunAgentInput? runAgentInput, List<ChatMessage> messages, List<ChatResponseUpdate> updates)
    {
        RunAgentInput = runAgentInput;
        Messages = messages;
        Updates = updates;
    }

    internal RunAgentInput? RunAgentInput { get; }

    internal List<ChatMessage> Messages { get; }

    internal List<ChatResponseUpdate> Updates { get; }
}
