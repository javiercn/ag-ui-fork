using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Step06_RawEvents.Server;

/// <summary>
/// A stateless <see cref="DelegatingChatClient"/> that brackets the inner client's response
/// with opaque telemetry from an external system, surfacing each datum as an AG-UI
/// <see cref="RawEvent"/>.
/// </summary>
/// <remarks>
/// Each telemetry datum is wrapped in a <see cref="ChatResponseUpdate"/> whose
/// <see cref="ChatResponseUpdate.RawRepresentation"/> is a <see cref="RawEvent"/>. The hosting
/// layer's <c>AsAGUIEventStreamAsync</c> recognises <see cref="ChatResponseUpdate.RawRepresentation"/>
/// of type <see cref="BaseEvent"/> and emits the event verbatim, so no other plumbing is
/// required to inject protocol events into the stream.
/// </remarks>
internal sealed class TelemetryRawEventsChatClient : DelegatingChatClient
{
    private readonly TelemetrySource _telemetry;

    public TelemetryRawEventsChatClient(IChatClient innerClient, TelemetrySource telemetry)
        : base(innerClient)
    {
        _telemetry = telemetry;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var telemetry in _telemetry.GetPreCallEvents())
        {
            yield return ToChatResponseUpdate(telemetry);
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        foreach (var telemetry in _telemetry.GetPostCallEvents())
        {
            yield return ToChatResponseUpdate(telemetry);
        }
    }

    private static ChatResponseUpdate ToChatResponseUpdate(RawTelemetry telemetry) =>
        new()
        {
            RawRepresentation = new RawEvent
            {
                Event = telemetry.Payload,
                Source = telemetry.Source
            }
        };
}
