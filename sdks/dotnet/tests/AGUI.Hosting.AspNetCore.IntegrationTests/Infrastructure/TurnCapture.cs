using AGUI.Abstractions;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

internal sealed class TurnCapture
{
    internal TurnCapture(RunAgentInput input, List<BaseEvent> events)
    {
        Input = input;
        Events = events;
    }

    internal RunAgentInput Input { get; }

    internal List<BaseEvent> Events { get; }
}
