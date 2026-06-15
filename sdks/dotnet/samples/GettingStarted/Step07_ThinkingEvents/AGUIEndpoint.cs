using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step07_ThinkingEvents;

internal static class AGUIEndpoint
{
    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            [FromServices] IChatClient chatClient,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            // Track reasoning message state for the unmapped handler
            string? reasoningMessageId = null;
            bool reasoningStartEmitted = false;
            bool reasoningEndEmitted = false;

            var streamOptions = new AGUIStreamOptions()
                .MapContent(content =>
                {
                    if (content is TextReasoningContent reasoningContent)
                    {
                        return MapReasoningContent(
                            reasoningContent,
                            ref reasoningStartEmitted,
                            ref reasoningMessageId);
                    }

                    return null;
                });

            var ctx = input.ToChatRequestContext(jsonSerializerOptions, streamOptions);

            var events = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken)
                .AsAGUIEventStreamAsync(ctx, cancellationToken);

            // Wrap the event stream to inject reasoning end events before text starts
            var wrappedEvents = CloseReasoningBeforeText(
                events,
                () => reasoningStartEmitted,
                () => reasoningEndEmitted,
                v => reasoningEndEmitted = v,
                () => reasoningMessageId,
                cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(wrappedEvents, cancellationToken));
        });
    }

    private static IEnumerable<BaseEvent> MapReasoningContent(
        TextReasoningContent reasoningContent,
        ref bool reasoningStartEmitted,
        ref string? reasoningMessageId)
    {
        var events = new List<BaseEvent>();

        if (!reasoningStartEmitted)
        {
            reasoningStartEmitted = true;
            events.Add(new ReasoningStartEvent());
        }

        if (reasoningMessageId is null)
        {
            reasoningMessageId = $"msg_{Guid.NewGuid():N}";
            events.Add(new ReasoningMessageStartEvent
            {
                MessageId = reasoningMessageId
            });
        }

        if (!string.IsNullOrEmpty(reasoningContent.Text))
        {
            events.Add(new ReasoningMessageContentEvent
            {
                MessageId = reasoningMessageId,
                Delta = reasoningContent.Text
            });
        }

        return events;
    }

    private static async IAsyncEnumerable<BaseEvent> CloseReasoningBeforeText(
        IAsyncEnumerable<BaseEvent> events,
        Func<bool> getReasoningStartEmitted,
        Func<bool> getReasoningEndEmitted,
        Action<bool> setReasoningEndEmitted,
        Func<string?> getReasoningMessageId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // When we see a text message start, close the reasoning phase first
            if (evt is TextMessageStartEvent && getReasoningStartEmitted() && !getReasoningEndEmitted())
            {
                setReasoningEndEmitted(true);
                var msgId = getReasoningMessageId();
                if (msgId is not null)
                {
                    yield return new ReasoningMessageEndEvent { MessageId = msgId };
                }

                yield return new ReasoningEndEvent();
            }

            yield return evt;
        }
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}
