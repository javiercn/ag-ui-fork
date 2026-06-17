using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Hosting.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AGUI.Hosting.AspNetCore.IntegrationTests;

internal static class AGUIEndpointExtensions
{
    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, async (RunAgentInput input, HttpContext httpContext) =>
        {
            var jsonOptions = httpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>();
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;
            var cancellationToken = httpContext.RequestAborted;

            var chatClient = httpContext.RequestServices.GetRequiredService<IChatClient>();

            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            // Add server tools registered in DI alongside any approval-wrapped client tools
            // already installed by ToChatRequestContext.
            foreach (var tool in httpContext.RequestServices.GetServices<AITool>())
            {
                ctx.ChatOptions.Tools ??= new List<AITool>();
                ctx.ChatOptions.Tools.Add(tool);
            }

            var events = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken)
                .AsAGUIEventStreamAsync(ctx, cancellationToken);

            var result = new AGUIServerSentEventsResult(events);
            await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        });
    }
}
