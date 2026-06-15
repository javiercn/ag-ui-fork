using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace CrossLanguage.TestServer;

internal static class AgenticChatRoute
{
    // Plain pass-through chat: forward user messages straight to the LLM and
    // surface its streaming output as AG-UI events. Mirrors the dojo
    // "agentic_chat" backend's behaviour from the JS side.
    public static IEndpointConventionBuilder MapAgenticChat(
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

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            IAsyncEnumerable<ChatResponseUpdate> updates =
                chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken);

            IAsyncEnumerable<BaseEvent> events = updates.AsAGUIEventStreamAsync(ctx, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    internal static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}
