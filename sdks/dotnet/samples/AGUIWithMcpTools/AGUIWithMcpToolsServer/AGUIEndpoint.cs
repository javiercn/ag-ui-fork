using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace AGUIWithMcpToolsServer;

/// <summary>
/// AG-UI endpoint that exposes a chat client whose tool set is sourced
/// entirely from an MCP server. McpClientTool inherits from AIFunction so
/// the tools returned by <c>McpClient.ListToolsAsync()</c> can be passed
/// straight into <see cref="ChatOptions.Tools"/>.
/// </summary>
internal static class AGUIEndpoint
{
    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, async (
            [FromBody] RunAgentInput input,
            [FromServices] IChatClient chatClient,
            [FromServices] McpToolProvider mcpTools,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;
            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            // Pull the MCP tool descriptors once per request. In a long-lived
            // production server you'd cache these and refresh on a schedule.
            IReadOnlyList<AITool> serverTools = await mcpTools.GetToolsAsync(cancellationToken).ConfigureAwait(false);

            // Add server tools alongside any approval-wrapped client tools already
            // installed by ToChatRequestContext.
            ctx.ChatOptions.Tools ??= [];
            foreach (var tool in serverTools)
            {
                ctx.ChatOptions.Tools.Add(tool);
            }

            IAsyncEnumerable<ChatResponseUpdate> updates =
                chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken);

            IAsyncEnumerable<BaseEvent> events = updates.AsAGUIEventStreamAsync(ctx, cancellationToken);

            return TypedResults.ServerSentEvents(WrapAsSseItems(events, cancellationToken));
        });
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
