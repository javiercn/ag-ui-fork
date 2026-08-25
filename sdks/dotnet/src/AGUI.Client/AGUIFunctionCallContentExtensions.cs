using Microsoft.Extensions.AI;

namespace AGUI.Client;

/// <summary>
/// Extension methods for responding to function-backed AG-UI workflow interrupts.
/// </summary>
public static class AGUIFunctionCallContentExtensions
{
    /// <summary>
    /// Creates a function result that cancels the workflow interrupt represented by
    /// <paramref name="call"/>.
    /// </summary>
    /// <param name="call">The interrupted workflow function call.</param>
    /// <param name="result">An optional cancellation reason or payload.</param>
    /// <returns>A function result that AGUIChatClient sends with a cancelled Resume status.</returns>
    public static FunctionResultContent CreateCancellationResult(
        this FunctionCallContent call,
        object? result = null)
    {
        ArgumentNullThrowHelper.ThrowIfNull(call);

        return new FunctionResultContent(call.CallId, result)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [WorkflowInterruptRegistry.ResumeStatusKey] = AGUI.Abstractions.ResumeStatus.Cancelled,
            },
        };
    }
}
