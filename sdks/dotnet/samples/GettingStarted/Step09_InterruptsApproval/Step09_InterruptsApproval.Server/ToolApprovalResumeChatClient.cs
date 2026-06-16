using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace Step09_InterruptsApproval.Server;

/// <summary>
/// Bridges incoming <see cref="InterruptResponseContent"/> (produced by the SDK's
/// resume → message translation) into the MEAI tool-approval shape
/// (<see cref="ToolApprovalRequestContent"/> / <see cref="ToolApprovalResponseContent"/>)
/// that <see cref="FunctionInvokingChatClient"/> consumes. The wire payload is an
/// <see cref="AGUIToolApprovalResumePayload"/>; this wrapper decodes it and injects
/// the proper MEAI content into the message history before delegating.
/// </summary>
internal sealed class ToolApprovalResumeChatClient : DelegatingChatClient
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public ToolApprovalResumeChatClient(IChatClient innerClient, JsonSerializerOptions jsonSerializerOptions)
        : base(innerClient)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var rewritten = RewriteApprovalResumes(messages);
        return base.GetStreamingResponseAsync(rewritten, options, cancellationToken);
    }

    private List<ChatMessage> RewriteApprovalResumes(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            List<AIContent>? remaining = null;
            List<(ToolApprovalRequestContent Request, ToolApprovalResponseContent Response)>? approvals = null;

            for (int i = 0; i < message.Contents.Count; i++)
            {
                var content = message.Contents[i];

                if (content is InterruptResponseContent ir
                    && DecodeApprovalPayload(ir.Payload) is { } payload
                    && payload.ToolCall is { } tcInfo)
                {
                    remaining ??= CopyContents(message.Contents, i);

                    var toolCall = new FunctionCallContent(
                        callId: tcInfo.CallId ?? string.Empty,
                        name: tcInfo.Name ?? string.Empty,
                        arguments: tcInfo.Arguments);

                    var request = new ToolApprovalRequestContent(ir.RequestId, toolCall);
                    var response = new ToolApprovalResponseContent(ir.RequestId, payload.Approved, toolCall);

                    approvals ??= new();
                    approvals.Add((request, response));
                }
                else
                {
                    remaining?.Add(content);
                }
            }

            if (approvals is null)
            {
                result.Add(message);
                continue;
            }

            if (remaining is { Count: > 0 })
            {
                result.Add(new ChatMessage(message.Role, remaining));
            }

            // Emit the approval pair so FunctionInvokingChatClient sees the standard MEAI shape.
            foreach (var (request, response) in approvals)
            {
                result.Add(new ChatMessage(ChatRole.Assistant, [request]));
                result.Add(new ChatMessage(ChatRole.User, [response]));
            }
        }

        return result;
    }

    private AGUIToolApprovalResumePayload? DecodeApprovalPayload(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return (AGUIToolApprovalResumePayload?)JsonSerializer.Deserialize(
            element,
            _jsonSerializerOptions.GetTypeInfo(typeof(AGUIToolApprovalResumePayload)));
    }

    private static List<AIContent> CopyContents(IList<AIContent> contents, int upToExclusive)
    {
        var copy = new List<AIContent>(upToExclusive);
        for (int i = 0; i < upToExclusive; i++)
        {
            copy.Add(contents[i]);
        }
        return copy;
    }
}
