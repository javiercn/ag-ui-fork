using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AGUI.Abstractions;
using GitHub.Copilot;
using Microsoft.AspNetCore.Mvc;

namespace AGUIWithCopilotSdkServer;

/// <summary>
/// AG-UI endpoint that bridges a single <c>POST /agui</c> call to a
/// turn on a stateful <see cref="CopilotSession"/>. The key responsibilities:
///
/// <list type="number">
///   <item>Resolve the AG-UI ThreadId to a long-lived Copilot session via
///         <see cref="CopilotSessionRegistry"/>.</item>
///   <item>Extract only the *new* user prompt (Copilot already holds the
///         conversation history; replaying it would duplicate messages).</item>
///   <item>Subscribe to the session's event stream and translate each event
///         into the corresponding AG-UI event before flushing it out as SSE.</item>
///   <item>Complete the run on <see cref="SessionIdleEvent"/>.</item>
/// </list>
/// </summary>
internal static class CopilotAGUIEndpoint
{
    internal static IEndpointConventionBuilder MapCopilotAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, async (
            [FromBody] RunAgentInput input,
            [FromServices] CopilotSessionRegistry registry,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("CopilotAGUIEndpoint");
            CopilotSession session = await registry.GetOrCreateAsync(input.ThreadId, cancellationToken).ConfigureAwait(false);

            // Diff the AG-UI request against Copilot's durable event log
            // (loaded from disk by GetEventsAsync) so we forward exactly the
            // user prompts the session hasn't yet processed. No in-memory
            // mirror, no third-party store — the session IS the projection.
            (IReadOnlyList<AGUIUserMessage> newUserMessages, int aguiPrefix) =
                await CopilotHistoryDiff.ComputeNewUserMessagesAsync(session, input.Messages, cancellationToken)
                    .ConfigureAwait(false);

            logger.LogInformation(
                "Thread {ThreadId}: {Incoming} AG-UI messages, {Prefix} matched Copilot's projection, {New} new user prompts to forward",
                input.ThreadId, input.Messages.Count, aguiPrefix, newUserMessages.Count);

            // Concatenate any "extra" new user messages with a blank line
            // separator. The common case is exactly one, but a UI that
            // batches user input (or a retried request that doubles up)
            // shouldn't lose information.
            string combinedPrompt = string.Join("\n\n",
                newUserMessages.Select(CopilotHistoryDiff.FlattenUserContent));

            return TypedResults.ServerSentEvents(StreamTurnAsync(session, input, combinedPrompt, cancellationToken));
        });
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> StreamTurnAsync(
        CopilotSession session,
        RunAgentInput input,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SseItem<BaseEvent>(new RunStartedEvent
        {
            ThreadId = input.ThreadId,
            RunId = input.RunId,
        });

        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            yield return new SseItem<BaseEvent>(new RunFinishedEvent
            {
                ThreadId = input.ThreadId,
                RunId = input.RunId,
                Outcome = new RunFinishedSuccessOutcome(),
            });
            yield break;
        }

        // Unbounded channel: a Copilot session can emit many delta events in
        // quick succession during streaming, and dropping any of them would
        // break the assistant's surface text. Drops are not acceptable here,
        // so use Unbounded with SingleReader/SingleWriter for the fast path.
        Channel<BaseEvent> channel = Channel.CreateUnbounded<BaseEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        string? activeMessageId = null;

        IDisposable subscription = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageStartEvent start:
                    activeMessageId = start.Data.MessageId;
                    channel.Writer.TryWrite(new TextMessageStartEvent
                    {
                        MessageId = start.Data.MessageId,
                        Role = AGUIRoles.Assistant,
                    });
                    break;

                case AssistantMessageDeltaEvent delta:
                    string messageId = delta.Data.MessageId ?? activeMessageId ?? Guid.NewGuid().ToString("N");
                    if (!string.IsNullOrEmpty(delta.Data.DeltaContent))
                    {
                        channel.Writer.TryWrite(new TextMessageContentEvent
                        {
                            MessageId = messageId,
                            Delta = delta.Data.DeltaContent,
                        });
                    }
                    break;

                case AssistantMessageEvent message:
                    // Non-streaming completion (or trailing full-content event):
                    // surface it as a single content delta if we haven't already
                    // emitted any deltas for this message id, so the consumer
                    // sees the full text even when Streaming=false.
                    if (activeMessageId is null && !string.IsNullOrEmpty(message.Data.Content))
                    {
                        string mid = message.Data.MessageId ?? Guid.NewGuid().ToString("N");
                        channel.Writer.TryWrite(new TextMessageStartEvent
                        {
                            MessageId = mid,
                            Role = AGUIRoles.Assistant,
                        });
                        channel.Writer.TryWrite(new TextMessageContentEvent
                        {
                            MessageId = mid,
                            Delta = message.Data.Content,
                        });
                        channel.Writer.TryWrite(new TextMessageEndEvent
                        {
                            MessageId = mid,
                        });
                    }
                    else if (activeMessageId is not null)
                    {
                        channel.Writer.TryWrite(new TextMessageEndEvent
                        {
                            MessageId = activeMessageId,
                        });
                        activeMessageId = null;
                    }
                    break;

                case SessionIdleEvent:
                    // The Copilot turn is complete; close the channel so the
                    // outer enumerator falls through to RUN_FINISHED.
                    if (activeMessageId is not null)
                    {
                        channel.Writer.TryWrite(new TextMessageEndEvent
                        {
                            MessageId = activeMessageId,
                        });
                        activeMessageId = null;
                    }
                    channel.Writer.TryComplete();
                    break;
            }
        });

        try
        {
            // Send the prompt only after the subscription is wired up, so we
            // can't miss any events that fire synchronously inside SendAsync.
            await session.SendAsync(
                new MessageOptions { Prompt = userPrompt },
                cancellationToken).ConfigureAwait(false);

            await foreach (BaseEvent evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new SseItem<BaseEvent>(evt);
            }
        }
        finally
        {
            subscription.Dispose();
        }

        yield return new SseItem<BaseEvent>(new RunFinishedEvent
        {
            ThreadId = input.ThreadId,
            RunId = input.RunId,
            Outcome = new RunFinishedSuccessOutcome(),
        });
    }
}
