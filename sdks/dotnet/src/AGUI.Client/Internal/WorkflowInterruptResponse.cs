using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace AGUI.Client;

internal sealed class WorkflowInterruptResponse
{
    internal required FunctionCallContent Call { get; init; }

    internal required AGUIInterrupt Interrupt { get; init; }

    internal required FunctionResultContent Result { get; init; }

    internal required string ThreadId { get; init; }
}
