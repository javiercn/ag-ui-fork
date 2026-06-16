using System.Net.ServerSentEvents;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Step03_FrontendTools.Server;

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

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            var updates = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken);

            var events = updates.AsAGUIEventStreamAsync(ctx, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
    }

    private static async IAsyncEnumerable<SseItem<BaseEvent>> WrapAsSseItems(
        IAsyncEnumerable<BaseEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new SseItem<BaseEvent>(evt);
        }
    }
}
