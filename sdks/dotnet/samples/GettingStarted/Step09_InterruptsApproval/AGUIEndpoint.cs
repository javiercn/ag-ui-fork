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

namespace Step09_InterruptsApproval;

internal static class AGUIEndpoint
{
    internal static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, (
            [FromBody] RunAgentInput input,
            [FromServices] IChatClient chatClient,
            [FromServices] IEnumerable<AITool> tools,
            [FromServices] IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;
            var ctx = input.ToChatRequestContext(jsonSerializerOptions);

            // Add DI-provided server tools alongside any approval-wrapped client tools
            // already installed by ToChatRequestContext.
            ctx.ChatOptions.Tools ??= [];
            foreach (var tool in tools)
            {
                ctx.ChatOptions.Tools.Add(tool);
            }

            // Check for resume payload (tool approval) from a previous interrupt
            if (input.Resume is { Count: > 0 } resumeList)
            {
                var resume = resumeList[0];
                AGUIToolApprovalResumePayload? resumePayload = null;
                if (resume.Payload is { ValueKind: JsonValueKind.Object } payloadElement)
                {
                    resumePayload = JsonSerializer.Deserialize(
                        payloadElement.GetRawText(),
                        jsonSerializerOptions.GetTypeInfo(typeof(AGUIToolApprovalResumePayload))) as AGUIToolApprovalResumePayload;
                }

                if (resumePayload is not null)
                {
                    var toolCall = new FunctionCallContent(
                        resumePayload.ToolCall?.CallId ?? string.Empty,
                        resumePayload.ToolCall?.Name ?? string.Empty,
                        resumePayload.ToolCall?.Arguments);
                    var approvalRequest = new ToolApprovalRequestContent(resume.InterruptId, toolCall);
                    var approvalResponseContent = new ToolApprovalResponseContent(
                        resume.InterruptId, resumePayload.Approved, toolCall);

                    // Add to messages so FunctionInvokingChatClient can process the approval
                    ctx.Messages.Add(new ChatMessage(ChatRole.Assistant, [approvalRequest]));
                    ctx.Messages.Add(new ChatMessage(ChatRole.User, [approvalResponseContent]));
                }
            }

            var events = chatClient.GetStreamingResponseAsync(ctx.Messages, ctx.ChatOptions, cancellationToken)
                .AsAGUIEventStreamAsync(ctx, cancellationToken);

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
