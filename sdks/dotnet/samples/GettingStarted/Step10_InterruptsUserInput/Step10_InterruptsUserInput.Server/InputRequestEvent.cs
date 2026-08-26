namespace Step10_InterruptsUserInput.Server;

internal sealed class InputRequestEvent(string callId, string message)
{
    public string CallId { get; } = callId;

    public string Message { get; } = message;
}
